using BTCPayServer.Payments;
using BTCPayServer.Plugins.Payjoin.Models;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Payjoin;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

public sealed class PayjoinUriSessionService
{
    private static readonly Action<ILogger, string, Exception?> LogReceiverBuilderFailure =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(1, nameof(BuildAsync)),
            "Failed to build payjoin receiver session for invoice {InvoiceId}; falling back to plain BIP21.");
    private static readonly Action<ILogger, string, string, Exception?> LogExpectedPayjoinFallback =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(2, nameof(LogExpectedPayjoinFallback)),
            "Payjoin not enabled for invoice {InvoiceId}: {Reason}");
    private static readonly Action<ILogger, string, string, Exception?> LogUnexpectedPayjoinFallback =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(3, nameof(LogUnexpectedPayjoinFallback)),
            "Falling back to plain BIP21 for invoice {InvoiceId}: {Reason}");
    private static readonly Action<ILogger, string, Exception?> LogInvalidPersistedSessionRebuild =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(4, nameof(LogInvalidPersistedSessionRebuild)),
            "Persisted payjoin receiver session for invoice {InvoiceId} had an empty event log and will be rebuilt.");
    private static readonly Action<ILogger, string, Exception?> LogAddressMismatchedSessionRebuild =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(6, nameof(LogAddressMismatchedSessionRebuild)),
            "Persisted payjoin receiver session for invoice {InvoiceId} was built for a different address and will be rebuilt.");
    private static readonly Action<ILogger, string, Exception?> LogPayjoinUriCacheFailure =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(5, nameof(LogPayjoinUriCacheFailure)),
            "Could not cache the payjoin URI of the receiver session for invoice {InvoiceId}; the checkout render path will fall back to plain BIP21 for it.");
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly PayjoinReceiverSessionStore _receiverSessionStore;
    private readonly PayjoinMailroomManager _mailroomManager;
    private readonly PayjoinAvailabilityService _availabilityService;
    private readonly PayjoinSessionBuildLock _sessionBuildLock;
    private readonly IPayjoinAccountingBridgeService _accountingBridgeService;
    private readonly IPayjoinFeeRateProvider _feeRateProvider;
    private readonly ILogger<PayjoinUriSessionService> _logger;

    internal PayjoinUriSessionService(
        BTCPayNetworkProvider networkProvider,
        PayjoinReceiverSessionStore receiverSessionStore,
        PayjoinMailroomManager mailroomManager,
        PayjoinAvailabilityService availabilityService,
        PayjoinSessionBuildLock sessionBuildLock,
        IPayjoinAccountingBridgeService accountingBridgeService,
        IPayjoinFeeRateProvider feeRateProvider,
        ILogger<PayjoinUriSessionService> logger)
    {
        _networkProvider = networkProvider;
        _receiverSessionStore = receiverSessionStore;
        _mailroomManager = mailroomManager;
        _availabilityService = availabilityService;
        _sessionBuildLock = sessionBuildLock;
        _accountingBridgeService = accountingBridgeService;
        _feeRateProvider = feeRateProvider;
        _logger = logger;
    }

    internal async Task<PayjoinUriResult> BuildAsync(
        string cryptoCode,
        string destination,
        decimal due,
        PayjoinStoreSettings? storeSettings,
        bool enablePayjoin,
        string invoiceId,
        string storeId,
        DateTimeOffset monitoringExpiresAt,
        CancellationToken cancellationToken)
    {
        var network = _networkProvider.GetNetwork<BTCPayNetwork>(cryptoCode);
        if (network is null)
        {
            throw new InvalidOperationException($"Network not available for {cryptoCode}");
        }

        var bip21 = network.GenerateBIP21(destination, due).ToString();

        if (!enablePayjoin)
        {
            return LogExpectedFallbackAndReturnBip21(bip21, invoiceId, PayjoinAvailabilityStatus.DisabledByStore, PayjoinUnavailableReasons.DisabledByStoreSettings);
        }

        if (storeSettings is null)
        {
            return LogExpectedFallbackAndReturnBip21(bip21, invoiceId, PayjoinAvailabilityStatus.TemporarilyUnavailable, PayjoinUnavailableReasons.StoreSettingsUnavailable, retryable: false);
        }

        var directoryUrls = storeSettings.GetEffectiveDirectoryUrls();
        if (directoryUrls.Count == 0)
        {
            return LogExpectedFallbackAndReturnBip21(bip21, invoiceId, PayjoinAvailabilityStatus.MerchantRequirementsUnmet, PayjoinUnavailableReasons.DirectoryUrlsMissing);
        }

        var ohttpRelayUrls = storeSettings.GetEffectiveOhttpRelayUrls();

        if (ohttpRelayUrls.Count == 0)
        {
            return LogExpectedFallbackAndReturnBip21(bip21, invoiceId, PayjoinAvailabilityStatus.MerchantRequirementsUnmet, PayjoinUnavailableReasons.OhttpRelayUrlsMissing);
        }

        if (due <= 0m)
        {
            return LogExpectedFallbackAndReturnBip21(bip21, invoiceId, PayjoinAvailabilityStatus.InvoiceNotPayable, PayjoinUnavailableReasons.InvoiceAmountNotPositive);
        }

        if (!await _availabilityService.HasConfirmedReceiverInputsAsync(storeId, cryptoCode, network, cancellationToken).ConfigureAwait(false))
        {
            return LogExpectedFallbackAndReturnBip21(bip21, invoiceId, PayjoinAvailabilityStatus.TemporarilyUnavailable, PayjoinUnavailableReasons.NoConfirmedReceiverInputs);
        }

        try
        {
            using var sessionBuildLock = await _sessionBuildLock.AcquireAsync(invoiceId, cancellationToken).ConfigureAwait(false);
            PayjoinReceiverSessionState? session = null;
            if (_receiverSessionStore.TryGetSession(invoiceId, out var persistedSession) && persistedSession is not null)
            {
                PayjoinUriResult? DiscardForRebuild() =>
                    TryDiscardUnusableSession(invoiceId)
                        ? null
                        : LogExpectedFallbackAndReturnBip21(bip21, invoiceId, PayjoinAvailabilityStatus.TemporarilyUnavailable, PayjoinUnavailableReasons.SessionMidNegotiation);

                switch (persistedSession.GetServability().Decide(destination))
                {
                    case PayjoinPersistedSessionDecision.RebuildEmptyEventLog:
                        LogInvalidPersistedSessionRebuild(_logger, invoiceId, null);
                        if (DiscardForRebuild() is { } emptyLogFallback)
                        {
                            return emptyLogFallback;
                        }

                        break;

                    case PayjoinPersistedSessionDecision.RebuildAddressMismatch:
                        LogAddressMismatchedSessionRebuild(_logger, invoiceId, null);
                        if (DiscardForRebuild() is { } addressMismatchFallback)
                        {
                            return addressMismatchFallback;
                        }

                        break;

                    case PayjoinPersistedSessionDecision.NotServable:
                        return LogExpectedFallbackAndReturnBip21(bip21, invoiceId, PayjoinAvailabilityStatus.TemporarilyUnavailable, PayjoinUnavailableReasons.SessionNoLongerServable);

                    case PayjoinPersistedSessionDecision.Reuse:
                        session = persistedSession;
                        break;
                }
            }

            if (session is null)
            {
                var selectedRelay = await _mailroomManager.SelectBootstrapRouteAsync(
                    storeSettings,
                    storeId,
                    invoiceId,
                    cancellationToken).ConfigureAwait(false);

                if (selectedRelay is null)
                {
                    return LogUnexpectedFallbackAndReturnBip21(bip21, invoiceId, PayjoinAvailabilityStatus.TemporarilyUnavailable, PayjoinUnavailableReasons.OhttpKeysUnavailable);
                }

                // The accounting reset must precede session persistence. A crash between the two
                // steps then leaves a reset bridge without a session, and the retry simply resets
                // again (the reset is idempotent) before creating one. The reverse order left a
                // persisted session whose retry found it, skipped the reset, and carried the
                // previous session's accounting data forward.
                await EnsureAccountingBridgeAsync(invoiceId, storeId, cryptoCode, due, monitoringExpiresAt, resetForNewSession: true, cancellationToken).ConfigureAwait(false);

                var maxEffectiveFeeRateSatPerVb = await _feeRateProvider.GetMaxEffectiveFeeRateSatPerVbAsync(storeId, cancellationToken).ConfigureAwait(false);
                var bootstrapPersister = new CapturingReceiverSessionPersister();
                InitializeSession(destination, due, selectedRelay.DirectoryUrl.AbsoluteUri, selectedRelay.OhttpKeys, monitoringExpiresAt, maxEffectiveFeeRateSatPerVb, bootstrapPersister);
                session = _receiverSessionStore.GetOrCreateSession(
                    invoiceId,
                    destination,
                    storeId,
                    monitoringExpiresAt,
                    bootstrapPersister.Load());

                if (!session.GetServability().MatchesInvoice(destination))
                {
                    return LogUnexpectedFallbackAndReturnBip21(bip21, invoiceId, PayjoinAvailabilityStatus.TemporarilyUnavailable, PayjoinUnavailableReasons.SessionAddressOutdated);
                }
            }
            else
            {
                await EnsureAccountingBridgeAsync(invoiceId, storeId, cryptoCode, due, monitoringExpiresAt, resetForNewSession: false, cancellationToken).ConfigureAwait(false);
            }

            var persister = _receiverSessionStore.CreatePersister(session);

            using var replay = PayjoinMethods.ReplayReceiverEventLog(persister);
            using var history = replay.SessionHistory();
            using var pjUri = history.PjUri();
            var payjoinUri = pjUri.AsString();

            var verdict = PayjoinBip21.JudgeReplayedUri(payjoinUri, bip21, out var mergedPaymentUrl, out var mergeFault);
            if (verdict != PayjoinReplayedUriVerdict.Servable)
            {
                if (!IndictsTheInvoice(verdict) && !TryDiscardUnusableSession(invoiceId))
                {
                    return LogExpectedFallbackAndReturnBip21(bip21, invoiceId, PayjoinAvailabilityStatus.TemporarilyUnavailable, PayjoinUnavailableReasons.SessionMidNegotiation);
                }

                var reason = mergeFault is not null
                    ? PayjoinUnavailableReasons.PayjoinUriMergeCheckFaulted
                    : ReasonFor(verdict);
                return LogUnexpectedFallbackAndReturnBip21(
                    bip21,
                    invoiceId,
                    PayjoinAvailabilityStatus.TemporarilyUnavailable,
                    reason,
                    mergeFault,
                    retryable: IsRetryableVerdict(verdict, faulted: mergeFault is not null));
            }

            if (session.PayjoinUri is null)
            {
                CachePayjoinUri(invoiceId, destination, payjoinUri);
            }

            return PayjoinUriResult.Active(mergedPaymentUrl);
        }
        catch (UniffiException e)
        {
            var discarded = TryDiscardUnusableSession(invoiceId);
            LogReceiverBuilderFailure(_logger, invoiceId, e);

            if (e is IDisposable disposableFault)
            {
                disposableFault.Dispose();
            }

            return discarded
                ? PayjoinUriResult.Unavailable(bip21, PayjoinAvailabilityStatus.TemporarilyUnavailable, PayjoinUnavailableReasons.ReceiverSessionBuildFailed)
                : PayjoinUriResult.Unavailable(bip21, PayjoinAvailabilityStatus.TemporarilyUnavailable, PayjoinUnavailableReasons.SessionMidNegotiation);
        }
    }

    internal static string ReasonFor(PayjoinReplayedUriVerdict verdict) => verdict switch
    {
        PayjoinReplayedUriVerdict.Empty => PayjoinUnavailableReasons.EmptyPayjoinUri,
        PayjoinReplayedUriVerdict.NoPayjoinEndpoint => PayjoinUnavailableReasons.PayjoinUriWithoutEndpoint,
        PayjoinReplayedUriVerdict.MergeLostEndpoint => PayjoinUnavailableReasons.PayjoinUriMergeLostEndpoint,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Servable is not an unavailable verdict.")
    };

    internal static bool IsRetryableVerdict(PayjoinReplayedUriVerdict verdict, bool faulted) =>
        faulted || !IndictsTheInvoice(verdict);

    internal static bool IndictsTheInvoice(PayjoinReplayedUriVerdict verdict) =>
        verdict is PayjoinReplayedUriVerdict.MergeLostEndpoint;

    private bool TryDiscardUnusableSession(string invoiceId) =>
        _receiverSessionStore.TryRemoveSessionUnlessNegotiating(invoiceId);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A failed cache write must never change the payjoin availability answer that has already been determined.")]
    private void CachePayjoinUri(string invoiceId, string destination, string payjoinUri)
    {
        try
        {
            _receiverSessionStore.StorePayjoinUri(invoiceId, destination, payjoinUri);
        }
        catch (Exception e)
        {
            LogPayjoinUriCacheFailure(_logger, invoiceId, e);
        }
    }

    private static void InitializeSession(
        string destination,
        decimal due,
        string directoryUrl,
        OhttpKeys ohttpKeys,
        DateTimeOffset monitoringExpiresAt,
        ulong maxEffectiveFeeRateSatPerVb,
        JsonReceiverSessionPersister persister)
    {
        var amountSats = checked((ulong)Money.Coins(due).Satoshi);
        var expirationSecs = ToExpirationSeconds(monitoringExpiresAt);
        using var receiverBuilder = new ReceiverBuilder(destination, directoryUrl, ohttpKeys);
        using var builderWithAmount = receiverBuilder.WithAmount(amountSats);
        using var builderWithExpiration = builderWithAmount.WithExpiration(expirationSecs);
        using var builderWithMaxFeeRate = builderWithExpiration.WithMaxFeeRate(maxEffectiveFeeRateSatPerVb);
        using var transition = builderWithMaxFeeRate.Build();
        using var savedSession = transition.Save(persister);
    }

    internal static ulong ToExpirationSeconds(DateTimeOffset monitoringExpiresAt)
    {
        // Align the protocol session expiry with BTCPay's invoice monitoring window so the rust-payjoin
        // session does not expire independently (its 24h default) from the receiver's own cleanup deadline.
        // The FFI validates expiration against u32::MAX, so clamp accordingly.
        var remainingSeconds = (monitoringExpiresAt - DateTimeOffset.UtcNow).TotalSeconds;
        if (remainingSeconds < 1d)
        {
            return 1UL;
        }

        return (ulong)Math.Min(remainingSeconds, uint.MaxValue);
    }

    private async Task EnsureAccountingBridgeAsync(
        string invoiceId,
        string storeId,
        string cryptoCode,
        decimal due,
        DateTimeOffset monitoringExpiresAt,
        bool resetForNewSession,
        CancellationToken cancellationToken)
    {
        var effectiveInvoiceValueSats = due > 0m
            ? Money.Coins(due).Satoshi
            : (long?)null;

        await _accountingBridgeService.CreateOrGetAsync(
            new CreatePayjoinAccountingBridgeRequest(
                invoiceId,
                storeId,
                cryptoCode,
                PaymentTypes.CHAIN.GetPaymentMethodId(cryptoCode).ToString(),
                monitoringExpiresAt,
                EffectiveInvoiceValueSats: effectiveInvoiceValueSats),
            cancellationToken).ConfigureAwait(false);

        if (resetForNewSession)
        {
            await _accountingBridgeService.ResetForNewSessionAsync(invoiceId, effectiveInvoiceValueSats, monitoringExpiresAt, cancellationToken).ConfigureAwait(false);
        }
    }
    private PayjoinUriResult LogExpectedFallbackAndReturnBip21(string bip21, string invoiceId, PayjoinAvailabilityStatus status, string reason, bool? retryable = null)
    {
        LogExpectedPayjoinFallback(_logger, invoiceId, reason, null);
        return PayjoinUriResult.Unavailable(bip21, status, reason, retryable);
    }

    private PayjoinUriResult LogUnexpectedFallbackAndReturnBip21(string bip21, string invoiceId, PayjoinAvailabilityStatus status, string reason, Exception? fault = null, bool? retryable = null)
    {
        LogUnexpectedPayjoinFallback(_logger, invoiceId, reason, fault);

        if (fault is IDisposable disposableFault)
        {
            disposableFault.Dispose();
        }

        return PayjoinUriResult.Unavailable(bip21, status, reason, retryable);
    }
}
