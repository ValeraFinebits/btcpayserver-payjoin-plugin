using BTCPayServer.Payments;
using BTCPayServer.Plugins.Payjoin.Models;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Payjoin;
using System;
using System.Threading;
using System.Threading.Tasks;
using PayjoinUri = Payjoin.Uri;

namespace BTCPayServer.Plugins.Payjoin.Services;

public sealed class PayjoinUriSessionService
{
    private const string ReceiverSessionBuildFailedReason = "receiver session build failed";
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
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly PayjoinReceiverSessionStore _receiverSessionStore;
    private readonly PayjoinMailroomManager _mailroomManager;
    private readonly PayjoinAvailabilityService _availabilityService;
    private readonly PayjoinSessionBuildLock _sessionBuildLock;
    private readonly IPayjoinAccountingBridgeService _accountingBridgeService;
    private readonly ILogger<PayjoinUriSessionService> _logger;

    internal PayjoinUriSessionService(
        BTCPayNetworkProvider networkProvider,
        PayjoinReceiverSessionStore receiverSessionStore,
        PayjoinMailroomManager mailroomManager,
        PayjoinAvailabilityService availabilityService,
        PayjoinSessionBuildLock sessionBuildLock,
        IPayjoinAccountingBridgeService accountingBridgeService,
        ILogger<PayjoinUriSessionService> logger)
    {
        _networkProvider = networkProvider;
        _receiverSessionStore = receiverSessionStore;
        _mailroomManager = mailroomManager;
        _availabilityService = availabilityService;
        _sessionBuildLock = sessionBuildLock;
        _accountingBridgeService = accountingBridgeService;
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
            return LogExpectedFallbackAndReturnBip21(bip21, invoiceId, PayjoinAvailabilityStatus.DisabledByStore, "payjoin is disabled by store settings");
        }

        if (storeSettings is null)
        {
            return LogExpectedFallbackAndReturnBip21(bip21, invoiceId, PayjoinAvailabilityStatus.MerchantRequirementsUnmet, "store settings are unavailable");
        }

        var directoryUrls = storeSettings.GetEffectiveDirectoryUrls();
        if (directoryUrls.Count == 0)
        {
            return LogExpectedFallbackAndReturnBip21(bip21, invoiceId, PayjoinAvailabilityStatus.MerchantRequirementsUnmet, "directory URLs are missing");
        }

        var ohttpRelayUrls = storeSettings.GetEffectiveOhttpRelayUrls();

        if (ohttpRelayUrls.Count == 0)
        {
            return LogExpectedFallbackAndReturnBip21(bip21, invoiceId, PayjoinAvailabilityStatus.MerchantRequirementsUnmet, "OHTTP relay URLs are missing");
        }

        if (due <= 0m)
        {
            return LogExpectedFallbackAndReturnBip21(bip21, invoiceId, PayjoinAvailabilityStatus.InvoiceNotPayable, "invoice amount is not positive");
        }

        if (!await _availabilityService.HasConfirmedReceiverInputsAsync(storeId, cryptoCode, network, cancellationToken).ConfigureAwait(false))
        {
            return LogExpectedFallbackAndReturnBip21(bip21, invoiceId, PayjoinAvailabilityStatus.TemporarilyUnavailable, "no confirmed receiver inputs are available");
        }

        try
        {
            using var sessionBuildLock = await _sessionBuildLock.AcquireAsync(invoiceId, cancellationToken).ConfigureAwait(false);
            PayjoinReceiverSessionState? session = null;
            if (_receiverSessionStore.TryGetSession(invoiceId, out var persistedSession) && persistedSession is not null)
            {
                if (persistedSession.GetEvents().Length == 0)
                {
                    LogInvalidPersistedSessionRebuild(_logger, invoiceId, null);
                    _receiverSessionStore.RemoveSession(invoiceId);
                }
                else
                {
                    session = persistedSession;
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
                    return LogUnexpectedFallbackAndReturnBip21(bip21, invoiceId, PayjoinAvailabilityStatus.TemporarilyUnavailable, "OHTTP keys are unavailable from all configured relays");
                }

                // The accounting reset must precede session persistence. A crash between the two
                // steps then leaves a reset bridge without a session, and the retry simply resets
                // again (the reset is idempotent) before creating one. The reverse order left a
                // persisted session whose retry found it, skipped the reset, and carried the
                // previous session's accounting data forward.
                await EnsureAccountingBridgeAsync(invoiceId, storeId, cryptoCode, due, monitoringExpiresAt, resetForNewSession: true, cancellationToken).ConfigureAwait(false);

                var bootstrapPersister = new CapturingReceiverSessionPersister();
                InitializeSession(destination, due, selectedRelay.DirectoryUrl.AbsoluteUri, selectedRelay.OhttpKeys, monitoringExpiresAt, bootstrapPersister);
                session = _receiverSessionStore.CreateSession(
                    invoiceId,
                    destination,
                    storeId,
                    monitoringExpiresAt,
                    bootstrapPersister.Load());
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

            if (string.IsNullOrWhiteSpace(payjoinUri))
            {
                return LogUnexpectedFallbackAndReturnBip21(bip21, invoiceId, PayjoinAvailabilityStatus.TemporarilyUnavailable, "payjoin URI generation returned an empty value");
            }

            if (!HasSupportedPayjoinEndpoint(payjoinUri))
            {
                return LogUnexpectedFallbackAndReturnBip21(bip21, invoiceId, PayjoinAvailabilityStatus.TemporarilyUnavailable, "payjoin URI does not advertise payjoin support");
            }

            return PayjoinUriResult.Active(payjoinUri);
        }
        catch (Exception e) when (e is ReceiverReplayException or UniffiException)
        {
            _receiverSessionStore.RemoveSession(invoiceId);
            LogReceiverBuilderFailure(_logger, invoiceId, e);
            return PayjoinUriResult.Unavailable(bip21, PayjoinAvailabilityStatus.TemporarilyUnavailable, ReceiverSessionBuildFailedReason);
        }
    }

    // TODO (M1 follow-up): replace this fixed cap with NBXplorer fee estimation and/or a per-store setting.
    // The receiver only pays the additional fee on its own contributed input/output, so a generous cap
    // avoids silently failing payjoin in high-fee environments while bounding griefing to that contribution.
    private const ulong DefaultMaxEffectiveFeeRateSatPerVb = 1000;

    private static void InitializeSession(
        string destination,
        decimal due,
        string directoryUrl,
        OhttpKeys ohttpKeys,
        DateTimeOffset monitoringExpiresAt,
        JsonReceiverSessionPersister persister)
    {
        var amountSats = checked((ulong)Money.Coins(due).Satoshi);
        var expirationSecs = ToExpirationSeconds(monitoringExpiresAt);
        using var receiverBuilder = new ReceiverBuilder(destination, directoryUrl, ohttpKeys);
        using var builderWithAmount = receiverBuilder.WithAmount(amountSats);
        using var builderWithExpiration = builderWithAmount.WithExpiration(expirationSecs);
        using var builderWithMaxFeeRate = builderWithExpiration.WithMaxFeeRate(DefaultMaxEffectiveFeeRateSatPerVb);
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

    internal static bool HasSupportedPayjoinEndpoint(string paymentUrl)
    {
        try
        {
            using var parsedUri = PayjoinUri.Parse(paymentUrl);
            using var _ = parsedUri.CheckPjSupported();
            return true;
        }
        catch (UriParseException)
        {
            return false;
        }
        catch (PjNotSupported)
        {
            return false;
        }
        catch (UniffiException)
        {
            return false;
        }
    }

    private PayjoinUriResult LogExpectedFallbackAndReturnBip21(string bip21, string invoiceId, PayjoinAvailabilityStatus status, string reason)
    {
        LogExpectedPayjoinFallback(_logger, invoiceId, reason, null);
        return PayjoinUriResult.Unavailable(bip21, status, reason);
    }

    private PayjoinUriResult LogUnexpectedFallbackAndReturnBip21(string bip21, string invoiceId, PayjoinAvailabilityStatus status, string reason)
    {
        LogUnexpectedPayjoinFallback(_logger, invoiceId, reason, null);
        return PayjoinUriResult.Unavailable(bip21, status, reason);
    }
}
