using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Wallets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using Payjoin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using SystemUri = System.Uri;

namespace BTCPayServer.Plugins.Payjoin.Services;

public sealed class PayjoinReceiverPoller : BackgroundService
{
    private static readonly TimeSpan RelayRequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly Action<ILogger, Exception?> LogPayjoinReceiverPollingFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(1, nameof(LogPayjoinReceiverPollingFailed)),
            "Payjoin receiver polling failed.");
    private static readonly Action<ILogger, string, Exception?> LogPayjoinReceiverPollingFailedForInvoice =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(2, nameof(LogPayjoinReceiverPollingFailedForInvoice)),
            "Payjoin receiver polling failed for {InvoiceId}");
    private static readonly Action<ILogger, string, Exception?> LogPayjoinReceiverScriptUnavailable =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(3, nameof(LogPayjoinReceiverScriptUnavailable)),
            "Payjoin receiver script unavailable for {InvoiceId}");
    private static readonly Action<ILogger, string, Exception?> LogPayjoinReceiverRelayUrlUnavailable =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(4, nameof(LogPayjoinReceiverRelayUrlUnavailable)),
            "Payjoin receiver relay URL unavailable for {InvoiceId}");
    private static readonly Action<ILogger, string, string, Exception?> LogPayjoinReceiverReplayFailed =
        LoggerMessage.Define<string, string>(LogLevel.Warning, new EventId(5, nameof(LogPayjoinReceiverReplayFailed)),
            "Payjoin receiver replay failed for {InvoiceId}: {Message}");
    private static readonly Action<ILogger, string, Exception?> LogPayjoinReceiverPreparedExactPaymentOutputs =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(6, nameof(LogPayjoinReceiverPreparedExactPaymentOutputs)),
            "Payjoin receiver prepared exact-payment outputs with dedicated receiver change for {InvoiceId}");
    private static readonly Action<ILogger, string, Exception?> LogPayjoinReceiverExactPaymentOutputsUnavailable =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(7, nameof(LogPayjoinReceiverExactPaymentOutputsUnavailable)),
            "Payjoin receiver exact-payment outputs unavailable for {InvoiceId}");
    private static readonly Action<ILogger, string, string, Exception?> LogPayjoinReceiverExactPaymentOutputsFailed =
        LoggerMessage.Define<string, string>(LogLevel.Warning, new EventId(8, nameof(LogPayjoinReceiverExactPaymentOutputsFailed)),
            "Payjoin receiver failed to prepare exact-payment outputs for {InvoiceId}: {Message}");
    private static readonly Action<ILogger, string, string, Exception?> LogPayjoinReceiverSessionRemoved =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(9, nameof(LogPayjoinReceiverSessionRemoved)),
            "Payjoin receiver session removed for {InvoiceId}: {Reason}");
    private static readonly Action<ILogger, string, string, Exception?> LogPayjoinReceiverInputContributionFailed =
        LoggerMessage.Define<string, string>(LogLevel.Warning, new EventId(10, nameof(LogPayjoinReceiverInputContributionFailed)),
            "Payjoin receiver could not contribute inputs for {InvoiceId}: {Message}");
    private static readonly Action<ILogger, string, Exception?> LogPayjoinReceiverPersistedInputUnavailable =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(11, nameof(LogPayjoinReceiverPersistedInputUnavailable)),
            "Payjoin receiver persisted contributed input unavailable for {InvoiceId}");

    private readonly PayjoinReceiverSessionStore _sessionStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PayjoinReceiverPoller> _logger;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly StoreRepository _storeRepository;
    private readonly InvoiceRepository _invoiceRepository;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly PayjoinAvailabilityService _availabilityService;
    private readonly ExplorerClientProvider _explorerClientProvider;
    private readonly IPayjoinStoreSettingsRepository _storeSettingsRepository;

    public PayjoinReceiverPoller(
        PayjoinReceiverSessionStore sessionStore,
        IHttpClientFactory httpClientFactory,
        BTCPayNetworkProvider networkProvider,
        StoreRepository storeRepository,
        InvoiceRepository invoiceRepository,
        PaymentMethodHandlerDictionary handlers,
        PayjoinAvailabilityService availabilityService,
        ExplorerClientProvider explorerClientProvider,
        IPayjoinStoreSettingsRepository storeSettingsRepository,
        ILogger<PayjoinReceiverPoller> logger)
    {
        _sessionStore = sessionStore;
        _httpClientFactory = httpClientFactory;
        _networkProvider = networkProvider;
        _storeRepository = storeRepository;
        _invoiceRepository = invoiceRepository;
        _handlers = handlers;
        _availabilityService = availabilityService;
        _explorerClientProvider = explorerClientProvider;
        _storeSettingsRepository = storeSettingsRepository;
        _logger = logger;
    }

    // TODO: Process sessions concurrently instead of sequentially.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            foreach (var session in _sessionStore.GetSessions())
            {
                try
                {
                    await ProcessSessionAsync(session, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (HttpRequestException ex)
                {
                    LogPayjoinReceiverPollingFailedForInvoice(_logger, session.InvoiceId, ex);
                }
                catch (InvalidOperationException ex)
                {
                    LogPayjoinReceiverPollingFailedForInvoice(_logger, session.InvoiceId, ex);
                    RemoveSession(session.InvoiceId, "receiver session failed with invalid operation");
                }
                catch (TaskCanceledException ex)
                {
                    LogPayjoinReceiverPollingFailedForInvoice(_logger, session.InvoiceId, ex);
                }
                catch (UniffiException ex)
                {
                    LogPayjoinReceiverPollingFailedForInvoice(_logger, session.InvoiceId, ex);
                    RemoveSession(session.InvoiceId, "receiver session failed with uniffi error");
                }
                catch (Exception ex)
                {
                    LogPayjoinReceiverPollingFailed(_logger, ex);
                    throw;
                }
            }
        }
    }

    private async Task ProcessSessionAsync(PayjoinReceiverSessionState session, CancellationToken stoppingToken)
    {
        var refreshedSession = await RefreshInvoiceCloseStateAsync(session).ConfigureAwait(false);
        if (refreshedSession is null)
        {
            return;
        }
        session = refreshedSession;

        if (!TryGetReceiverScript(session, out var receiverScript))
        {
            LogPayjoinReceiverScriptUnavailable(_logger, session.InvoiceId, null);
            return;
        }

        if (session.OhttpRelayUrl is null)
        {
            LogPayjoinReceiverRelayUrlUnavailable(_logger, session.InvoiceId, null);
            return;
        }

        var persister = _sessionStore.CreatePersister(session);
        ReplayResult replay;
        try
        {
            replay = PayjoinMethods.ReplayReceiverEventLog(persister);
        }
        catch (ReceiverReplayException ex)
        {
            LogPayjoinReceiverReplayFailed(_logger, session.InvoiceId, ex.Message, ex);
            RemoveSession(session.InvoiceId, "receiver replay failed");
            return;
        }

        using var replayScope = replay;
        using var state = replayScope.State();

        if (TryExpireSession(session))
        {
            return;
        }

        if (TryRemoveCloseRequestedSession(session, state))
        {
            return;
        }

        switch (state)
        {
            case ReceiveSession.Initialized initialized:
                await PollInitializedAsync(session, initialized.inner, persister, receiverScript, session.OhttpRelayUrl, session.StoreId, session.InvoiceId, stoppingToken).ConfigureAwait(false);
                break;
            case ReceiveSession.HasReplyableError hasReplyableError:
                await ProcessReplyableErrorAsync(hasReplyableError.inner, persister, session.OhttpRelayUrl, stoppingToken).ConfigureAwait(false);
                break;
            case ReceiveSession.UncheckedOriginalPayload payload:
                await ProcessUncheckedProposalAsync(session, payload.inner, persister, receiverScript, session.OhttpRelayUrl, session.StoreId, session.InvoiceId, stoppingToken).ConfigureAwait(false);
                break;
            case ReceiveSession.MaybeInputsOwned maybeInputsOwned:
                await ProcessMaybeInputsOwnedAsync(maybeInputsOwned.inner, persister, receiverScript, session.OhttpRelayUrl, session.StoreId, session.InvoiceId, stoppingToken).ConfigureAwait(false);
                break;
            case ReceiveSession.MaybeInputsSeen maybeInputsSeen:
                await ProcessMaybeInputsSeenAsync(maybeInputsSeen.inner, persister, receiverScript, session.OhttpRelayUrl, session.StoreId, session.InvoiceId, stoppingToken).ConfigureAwait(false);
                break;
            case ReceiveSession.OutputsUnknown outputsUnknown:
                await ProcessOutputsUnknownAsync(outputsUnknown.inner, persister, receiverScript, session.OhttpRelayUrl, session.StoreId, session.InvoiceId, stoppingToken).ConfigureAwait(false);
                break;
            case ReceiveSession.WantsOutputs wantsOutputs:
                await ProcessWantsOutputsAsync(wantsOutputs.inner, persister, receiverScript, session.OhttpRelayUrl, session.StoreId, session.InvoiceId, stoppingToken).ConfigureAwait(false);
                break;
            case ReceiveSession.WantsInputs wantsInputs:
                await ProcessWantsInputsAsync(wantsInputs.inner, persister, receiverScript, session.OhttpRelayUrl, session.StoreId, session.InvoiceId, stoppingToken).ConfigureAwait(false);
                break;
            case ReceiveSession.WantsFeeRange wantsFeeRange:
            {
                var contributedCoinsForFeeRange = await TryGetRequiredPersistedContributedCoinsAsync(session, "persisted receiver input unavailable", stoppingToken).ConfigureAwait(false);
                if (contributedCoinsForFeeRange is null)
                {
                    return;
                }

                await ProcessWantsFeeRangeAsync(wantsFeeRange.inner, persister, receiverScript, session.OhttpRelayUrl, session.StoreId, session.InvoiceId, contributedCoinsForFeeRange, stoppingToken).ConfigureAwait(false);
                break;
            }
            case ReceiveSession.ProvisionalProposal provisionalProposal:
            {
                var contributedCoinsForProposal = await TryGetRequiredPersistedContributedCoinsAsync(session, "persisted receiver input unavailable", stoppingToken).ConfigureAwait(false);
                if (contributedCoinsForProposal is null)
                {
                    return;
                }

                await ProcessProvisionalProposalAsync(provisionalProposal.inner, persister, receiverScript, session.OhttpRelayUrl, session.StoreId, session.InvoiceId, contributedCoinsForProposal, stoppingToken).ConfigureAwait(false);
                break;
            }
            case ReceiveSession.PayjoinProposal payjoinProposal:
                await PostPayjoinProposalAsync(payjoinProposal.inner, persister, session.OhttpRelayUrl, stoppingToken).ConfigureAwait(false);
                break;
            case ReceiveSession.Monitor:
                break;
            case ReceiveSession.Closed:
                RemoveSession(session.InvoiceId, "receiver session closed");
                break;
        }
    }

    private async Task<PayjoinReceiverSessionState?> RefreshInvoiceCloseStateAsync(PayjoinReceiverSessionState session)
    {
        var invoice = await _invoiceRepository.GetInvoice(session.InvoiceId).ConfigureAwait(false);
        if (invoice is null)
        {
            RemoveSession(session.InvoiceId, "invoice no longer exists");
            return null;
        }

        if (invoice.GetInvoiceState().Status != InvoiceStatus.New)
        {
            var status = invoice.GetInvoiceState().Status;
            _sessionStore.RequestClose(session.InvoiceId, status);
            return _sessionStore.TryGetSession(session.InvoiceId, out var closeRequestedSession)
                ? closeRequestedSession
                : null;
        }

        return session;
    }

    internal bool TryExpireSession(PayjoinReceiverSessionState session)
    {
        var now = DateTimeOffset.UtcNow;
        var deadline = GetCleanupDeadline(session);
        if (now >= deadline)
        {
            return RemoveSession(session.InvoiceId, $"cleanup deadline reached at {deadline:O}");
        }

        return false;
    }

    private static DateTimeOffset GetCleanupDeadline(PayjoinReceiverSessionState session)
    {
        return session.MonitoringExpiresAt;
    }

    private bool RemoveSession(string invoiceId, string reason)
    {
        if (!_sessionStore.RemoveSession(invoiceId))
        {
            return false;
        }

        LogPayjoinReceiverSessionRemoved(_logger, invoiceId, reason, null);
        return true;
    }

    internal bool TryRemoveCloseRequestedSession(PayjoinReceiverSessionState session, ReceiveSession state)
    {
        if (!session.IsCloseRequested)
        {
            return false;
        }

        if (StateCanStillReplyAfterCloseRequest(session, state))
        {
            return false;
        }

        return RemoveCloseRequestedSession(session);
    }

    private static bool StateCanStillReplyAfterCloseRequest(PayjoinReceiverSessionState session, ReceiveSession state)
    {
        if (state is ReceiveSession.UncheckedOriginalPayload or ReceiveSession.HasReplyableError)
        {
            return true;
        }

        return state is ReceiveSession.Initialized && session.CanPollInitializedAfterCloseRequest();
    }

    private bool RemoveCloseRequestedSession(PayjoinReceiverSessionState session)
    {
        return RemoveSession(session.InvoiceId, $"invoice is no longer payable ({session.CloseInvoiceStatus})");
    }

    private async Task PollInitializedAsync(
        PayjoinReceiverSessionState session,
        Initialized initialized,
        JsonReceiverSessionPersister persister,
        byte[] receiverScript,
        SystemUri ohttpRelayUrl,
        string storeId,
        string invoiceId,
        CancellationToken stoppingToken)
    {
        if (session.IsCloseRequested)
        {
            _sessionStore.TryConsumeInitializedPollAfterCloseRequest(session.InvoiceId);
        }

        using var requestResponse = initialized.CreatePollRequest(ohttpRelayUrl.ToString());
        var request = requestResponse.request;

        using var message = new HttpRequestMessage(HttpMethod.Post, request.url)
        {
            Content = new ByteArrayContent(request.body)
        };
        message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.contentType);

        var client = _httpClientFactory.CreateClient(nameof(PayjoinReceiverPoller));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeoutCts.CancelAfter(RelayRequestTimeout);
        using var response = await client.SendAsync(message, timeoutCts.Token).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsByteArrayAsync(timeoutCts.Token).ConfigureAwait(false);

        using var transition = initialized.ProcessResponse(responseBody, requestResponse.clientResponse);
        using var outcome = transition.Save(persister);

        if (outcome is InitializedTransitionOutcome.Progress progress)
        {
            await ProcessUncheckedProposalAsync(session, progress.inner, persister, receiverScript, ohttpRelayUrl, storeId, invoiceId, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessUncheckedProposalAsync(
        PayjoinReceiverSessionState session,
        UncheckedOriginalPayload proposal,
        JsonReceiverSessionPersister persister,
        byte[] receiverScript,
        SystemUri ohttpRelayUrl,
        string storeId,
        string invoiceId,
        CancellationToken stoppingToken)
    {
        if (session.IsCloseRequested)
        {
            if (await TryRejectCloseRequestedOriginalPayloadAsync(session, proposal, persister, ohttpRelayUrl, stoppingToken).ConfigureAwait(false))
            {
                return;
            }

            RemoveCloseRequestedSession(session);
            return;
        }

        using var transition = proposal.AssumeInteractiveReceiver();
        using var maybeInputsOwned = transition.Save(persister);
        await ProcessMaybeInputsOwnedAsync(maybeInputsOwned, persister, receiverScript, ohttpRelayUrl, storeId, invoiceId, stoppingToken).ConfigureAwait(false);
    }

    private async Task<bool> TryRejectCloseRequestedOriginalPayloadAsync(
        PayjoinReceiverSessionState session,
        UncheckedOriginalPayload proposal,
        JsonReceiverSessionPersister persister,
        SystemUri ohttpRelayUrl,
        CancellationToken stoppingToken)
    {
        // TODO: Replace this close-request workaround with a direct rust-payjoin/payjoin-ffi API for
        // creating a replyable receiver rejection from the current session state. The current bindings
        // do not expose persisted `error_state()` or an explicit `Unavailable`/session-closed reject path,
        // so we temporarily route invoice-closed sessions through `CheckBroadcastSuitability`.
        using var rejectionTransition = proposal.CheckBroadcastSuitability(null, new CloseRequestedBroadcastGuard(session));

        try
        {
            using var _ = rejectionTransition.Save(persister);
            return false;
        }
        catch (ReceiverPersistedException ex)
        {
            if (await TryPostPersistedReplyableErrorAsync(persister, ohttpRelayUrl, stoppingToken).ConfigureAwait(false))
            {
                return true;
            }

            ExceptionDispatchInfo.Capture(ex).Throw();
            throw;
        }
    }

    private async Task<bool> TryPostPersistedReplyableErrorAsync(
        JsonReceiverSessionPersister persister,
        SystemUri ohttpRelayUrl,
        CancellationToken stoppingToken)
    {
        ReplayResult replay;
        try
        {
            replay = PayjoinMethods.ReplayReceiverEventLog(persister);
        }
        catch (ReceiverReplayException)
        {
            return false;
        }

        using var replayScope = replay;
        using var replayState = replayScope.State();

        if (replayState is not ReceiveSession.HasReplyableError hasReplyableError)
        {
            return false;
        }

        await ProcessReplyableErrorAsync(hasReplyableError.inner, persister, ohttpRelayUrl, stoppingToken).ConfigureAwait(false);
        return true;
    }

    private async Task ProcessReplyableErrorAsync(
        HasReplyableError replyableError,
        JsonReceiverSessionPersister persister,
        SystemUri ohttpRelayUrl,
        CancellationToken stoppingToken)
    {
        using var requestResponse = replyableError.CreateErrorRequest(ohttpRelayUrl.ToString());
        var responseBody = await SendRelayRequestAsync(requestResponse.request, stoppingToken).ConfigureAwait(false);
        using var transition = replyableError.ProcessErrorResponse(responseBody, requestResponse.clientResponse);
        transition.Save(persister);
    }

    private async Task ProcessMaybeInputsOwnedAsync(
        MaybeInputsOwned proposal,
        JsonReceiverSessionPersister persister,
        byte[] receiverScript,
        SystemUri ohttpRelayUrl,
        string storeId,
        string invoiceId,
        CancellationToken stoppingToken)
    {
        using var transition = proposal.CheckInputsNotOwned(new ReceiverScriptOwnedCallback(receiverScript));
        using var maybeInputsSeen = transition.Save(persister);
        await ProcessMaybeInputsSeenAsync(maybeInputsSeen, persister, receiverScript, ohttpRelayUrl, storeId, invoiceId, stoppingToken).ConfigureAwait(false);
    }

    private async Task ProcessMaybeInputsSeenAsync(
        MaybeInputsSeen proposal,
        JsonReceiverSessionPersister persister,
        byte[] receiverScript,
        SystemUri ohttpRelayUrl,
        string storeId,
        string invoiceId,
        CancellationToken stoppingToken)
    {
        using var transition = proposal.CheckNoInputsSeenBefore(new NoInputsSeenCallback());
        using var outputsUnknown = transition.Save(persister);
        await ProcessOutputsUnknownAsync(outputsUnknown, persister, receiverScript, ohttpRelayUrl, storeId, invoiceId, stoppingToken).ConfigureAwait(false);
    }

    private async Task ProcessOutputsUnknownAsync(
        OutputsUnknown proposal,
        JsonReceiverSessionPersister persister,
        byte[] receiverScript,
        SystemUri ohttpRelayUrl,
        string storeId,
        string invoiceId,
        CancellationToken stoppingToken)
    {
        using var transition = proposal.IdentifyReceiverOutputs(new ReceiverScriptOwnedCallback(receiverScript));
        using var wantsOutputs = transition.Save(persister);
        await ProcessWantsOutputsAsync(wantsOutputs, persister, receiverScript, ohttpRelayUrl, storeId, invoiceId, stoppingToken).ConfigureAwait(false);
    }

    private async Task ProcessWantsOutputsAsync(
        WantsOutputs proposal,
        JsonReceiverSessionPersister persister,
        byte[] receiverScript,
        SystemUri ohttpRelayUrl,
        string storeId,
        string invoiceId,
        CancellationToken stoppingToken)
    {
        var exactPaymentOutputs = await TryCreateExactPaymentReceiverOutputsAsync(storeId, invoiceId, receiverScript, stoppingToken).ConfigureAwait(false);
        if (exactPaymentOutputs is null)
        {
            LogPayjoinReceiverExactPaymentOutputsUnavailable(_logger, invoiceId, null);
            RemoveSession(invoiceId, "exact-payment outputs unavailable");
            return;
        }

        try
        {
            using var modified = proposal.ReplaceReceiverOutputs(exactPaymentOutputs.Value.ExactPaymentOutputs, exactPaymentOutputs.Value.ReceiverChangeScript);
            using var transition = modified.CommitOutputs();
            using var wantsInputs = transition.Save(persister);
            LogPayjoinReceiverPreparedExactPaymentOutputs(_logger, invoiceId, null);
            await ProcessWantsInputsAsync(wantsInputs, persister, receiverScript, ohttpRelayUrl, storeId, invoiceId, stoppingToken).ConfigureAwait(false);
        }
        catch (OutputSubstitutionException ex)
        {
            LogPayjoinReceiverExactPaymentOutputsFailed(_logger, invoiceId, ex.Message, ex);
            RemoveSession(invoiceId, "failed to prepare exact-payment outputs");
        }
    }

    private async Task<(PlainTxOut[] ExactPaymentOutputs, byte[] ReceiverChangeScript)?> TryCreateExactPaymentReceiverOutputsAsync(
        string storeId,
        string invoiceId,
        byte[] receiverScript,
        CancellationToken cancellationToken)
    {
        // TODO: Add an explicit rust-payjoin / payjoin-ffi API for reading receiver outputs or original PSBT data.
        // TODO: Replace the invoice.Due fallback below with values read directly from the incoming payjoin proposal.
        // TODO: Validate that the invoice amount matches the receiver amount in the incoming proposal before building replacement outputs.
        var receiverChangeScript = await GetReceiverChangeScriptAsync(storeId, invoiceId, receiverScript, cancellationToken).ConfigureAwait(false);
        if (receiverChangeScript is null)
        {
            return null;
        }

        var exactPaymentAmountSats = await TryGetExactPaymentAmountSatsAsync(invoiceId).ConfigureAwait(false);
        if (exactPaymentAmountSats is null)
        {
            return null;
        }

        return CreateExactPaymentReceiverOutputs(exactPaymentAmountSats.Value, receiverScript, receiverChangeScript);
    }

    private async Task<ulong?> TryGetExactPaymentAmountSatsAsync(string invoiceId)
    {
        var invoice = await _invoiceRepository.GetInvoice(invoiceId).ConfigureAwait(false);
        var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId("BTC");
        var prompt = invoice?.GetPaymentPrompt(paymentMethodId);
        if (prompt is null)
        {
            return null;
        }

        var due = prompt.Calculate().Due;
        if (due <= 0m)
        {
            return null;
        }

        var dueSats = Money.Coins(due).Satoshi;
        if (dueSats <= 0)
        {
            return null;
        }

        return checked((ulong)dueSats);
    }

    internal static (PlainTxOut[] ExactPaymentOutputs, byte[] ReceiverChangeScript) CreateExactPaymentReceiverOutputs(
        ulong exactPaymentAmountSats,
        byte[] receiverScript,
        byte[] receiverChangeScript)
    {
        return (
            new[]
            {
                new PlainTxOut(exactPaymentAmountSats, receiverScript),
                new PlainTxOut(0, receiverChangeScript)
            },
            receiverChangeScript);
    }

    private async Task<byte[]?> GetReceiverChangeScriptAsync(
        string storeId,
        string invoiceId,
        byte[] receiverScript,
        CancellationToken cancellationToken)
    {
        var network = _networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        if (network is null)
        {
            return null;
        }

        var client = _explorerClientProvider.GetExplorerClient(network);

        var coldWalletDerivation = await TryParseColdWalletDerivationAsync(storeId, network).ConfigureAwait(false);
        if (coldWalletDerivation is not null)
        {
            var coldChangeAddress = await client.GetUnusedAsync(coldWalletDerivation, DerivationFeature.Change, 0, true, cancellationToken).ConfigureAwait(false);
            var coldChangeScript = coldChangeAddress?.ScriptPubKey?.ToBytes();
            if (coldChangeScript is not null && coldChangeScript.Length > 0 && !coldChangeScript.SequenceEqual(receiverScript))
            {
                return coldChangeScript;
            }
        }

        var store = await _storeRepository.FindStore(storeId).ConfigureAwait(false);
        if (store is null)
        {
            return null;
        }

        var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId("BTC");
        var derivationScheme = store.GetPaymentMethodConfig<DerivationSchemeSettings>(paymentMethodId, _handlers, true);
        if (derivationScheme is null)
        {
            return null;
        }

        var changeAddress = await client.GetUnusedAsync(derivationScheme.AccountDerivation, DerivationFeature.Change, 0, true, cancellationToken).ConfigureAwait(false);
        var generatedReceiverChangeScriptPubKey = changeAddress?.ScriptPubKey;
        if (generatedReceiverChangeScriptPubKey is null)
        {
            return null;
        }

        var generatedReceiverChangeScript = generatedReceiverChangeScriptPubKey.ToBytes();
        if (generatedReceiverChangeScript.SequenceEqual(receiverScript))
        {
            return null;
        }

        return generatedReceiverChangeScript;
    }

    private async Task<DerivationStrategyBase?> TryParseColdWalletDerivationAsync(string storeId, BTCPayNetwork network)
    {
        var storeSettings = await _storeSettingsRepository.GetAsync(storeId).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(storeSettings.ColdWalletDerivationScheme))
        {
            return null;
        }

        try
        {
            return DerivationSchemeHelper.Parse(storeSettings.ColdWalletDerivationScheme, network).AccountDerivation;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private async Task ProcessWantsInputsAsync(
        WantsInputs proposal,
        JsonReceiverSessionPersister persister,
        byte[] receiverScript,
        SystemUri ohttpRelayUrl,
        string storeId,
        string invoiceId,
        CancellationToken stoppingToken)
    {
        var (receiverInputs, receiverCoins) = await GetReceiverInputsAsync(storeId, invoiceId, stoppingToken).ConfigureAwait(false);

        WantsInputs? withInputs = null;
        ReceivedCoin[]? contributedCoins = null;
        var contributionFailures = new List<string>();
        try
        {
            // TODO: Restore `proposal.TryPreservingPrivacy(receiverInputs)` only after rust-payjoin/payjoin-ffi lets us identify which `ReceivedCoin` was actually selected.
            // TODO: We must persist only the truly contributed coin(s) into `contributedCoins`; otherwise the signing step can treat unrelated wallet inputs as receiver-owned and produce an invalid proposal.
            var orderedCandidates = receiverCoins
                .Select((coin, index) => new { coin, index })
                .OrderBy(x => x.coin.Coin.Amount.Satoshi)
                .Select(x => x.index);

            foreach (var index in orderedCandidates)
            {
                try
                {
                    withInputs = proposal.ContributeInputs(new[] { receiverInputs[index] });
                    contributedCoins = new[] { receiverCoins[index] };
                    break;
                }
                catch (InputContributionException ex)
                {
                    contributionFailures.Add($"candidate '{receiverCoins[index].OutPoint}' rejected: {ex.Message}");
                }
            }

            if (withInputs is null || contributedCoins is null)
            {
                var failureMessage = contributionFailures.Count switch
                {
                    > 3 => string.Join(" | ", contributionFailures.Take(3)) + " | ...",
                    > 0 => string.Join(" | ", contributionFailures),
                    _ => "no confirmed receiver coins available"
                };
                LogPayjoinReceiverInputContributionFailed(_logger, invoiceId, failureMessage, null);
                RemoveSession(invoiceId, "no receiver input available for payjoin contribution");
                return;
            }

            if (!_sessionStore.TryPersistContributedInput(invoiceId, contributedCoins[0].OutPoint))
            {
                RemoveSession(invoiceId, "failed to persist contributed receiver input");
                return;
            }
            using var transition = withInputs.CommitInputs();
            using var wantsFeeRange = transition.Save(persister);
            await ProcessWantsFeeRangeAsync(wantsFeeRange, persister, receiverScript, ohttpRelayUrl, storeId, invoiceId, contributedCoins, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            withInputs?.Dispose();
        }
    }

    private async Task<(InputPair[] Inputs, ReceivedCoin[] Coins)> GetReceiverInputsAsync(string storeId, string invoiceId, CancellationToken cancellationToken)
    {
        var network = _networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        if (network is null)
        {
            return (Array.Empty<InputPair>(), Array.Empty<ReceivedCoin>());
        }

        var confirmed = await _availabilityService.GetConfirmedReceiverCoinsAsync(storeId, "BTC", network, cancellationToken).ConfigureAwait(false);
        if (confirmed.Length == 0)
        {
            return (Array.Empty<InputPair>(), Array.Empty<ReceivedCoin>());
        }

        var inputs = confirmed
            .Select(c =>
            {
                var txin = new PlainTxIn(
                    new PlainOutPoint(c.OutPoint.Hash.ToString(), (uint)c.OutPoint.N),
                    Array.Empty<byte>(),
                    uint.MaxValue,
                    Array.Empty<byte[]>());
                var txout = new PlainTxOut(checked((ulong)c.Coin.Amount.Satoshi), c.ScriptPubKey.ToBytes());
                var psbtIn = new PlainPsbtInput(txout, null, null);
                return new InputPair(txin, psbtIn, null);
            })
            .ToArray();

        return (inputs, confirmed);
    }

    private async Task ProcessWantsFeeRangeAsync(
        WantsFeeRange proposal,
        JsonReceiverSessionPersister persister,
        byte[] receiverScript,
        SystemUri ohttpRelayUrl,
        string storeId,
        string invoiceId,
        ReceivedCoin[] receiverCoins,
        CancellationToken stoppingToken)
    {
        // TODO: Replace hardcoded fee range with values from NBXplorer fee estimation.
        using var transition = proposal.ApplyFeeRange(1, 10);
        using var provisional = transition.Save(persister);
        await ProcessProvisionalProposalAsync(provisional, persister, receiverScript, ohttpRelayUrl, storeId, invoiceId, receiverCoins, stoppingToken).ConfigureAwait(false);
    }

    private async Task<ReceivedCoin[]?> TryGetRequiredPersistedContributedCoinsAsync(
        PayjoinReceiverSessionState session,
        string removalReason,
        CancellationToken cancellationToken)
    {
        var contributedCoins = await TryGetPersistedContributedCoinsAsync(session, cancellationToken).ConfigureAwait(false);
        if (contributedCoins is not null)
        {
            return contributedCoins;
        }

        LogPayjoinReceiverPersistedInputUnavailable(_logger, session.InvoiceId, null);
        RemoveSession(session.InvoiceId, removalReason);
        return null;
    }

    internal async Task<ReceivedCoin[]?> TryGetPersistedContributedCoinsAsync(PayjoinReceiverSessionState session, CancellationToken cancellationToken)
    {
        if (!session.TryGetContributedInput(out var contributedOutPoint))
        {
            return null;
        }

        var (_, receiverCoins) = await GetReceiverInputsAsync(session.StoreId, session.InvoiceId, cancellationToken).ConfigureAwait(false);
        var contributedCoins = receiverCoins
            .Where(coin => coin.OutPoint.Hash == contributedOutPoint.Hash && coin.OutPoint.N == contributedOutPoint.N)
            .ToArray();
        return contributedCoins.Length > 0 ? contributedCoins : null;
    }

    private async Task ProcessProvisionalProposalAsync(
        ProvisionalProposal proposal,
        JsonReceiverSessionPersister persister,
        byte[] receiverScript,
        SystemUri ohttpRelayUrl,
        string storeId,
        string invoiceId,
        ReceivedCoin[] receiverCoins,
        CancellationToken stoppingToken)
    {
        // TODO: Extract receiver proposal signing and PSBT normalization into a dedicated service so this poller stays focused on session orchestration.
        var network = _networkProvider.GetNetwork<BTCPayNetwork>("BTC") ?? throw new InvalidOperationException("BTC network not available");
        var store = await _storeRepository.FindStore(storeId).ConfigureAwait(false) ?? throw new InvalidOperationException($"Store {storeId} not found");
        var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId("BTC");
        var derivationScheme = store.GetPaymentMethodConfig<DerivationSchemeSettings>(paymentMethodId, _handlers, true) ?? throw new InvalidOperationException("Derivation scheme not configured for BTC");

        if (!derivationScheme.IsHotWallet)
        {
            throw new InvalidOperationException("Cannot sign payjoin proposal from a cold wallet");
        }

        var client = _explorerClientProvider.GetExplorerClient(network);
        var signingKeyStr = await client.GetMetadataAsync<string>(
            derivationScheme.AccountDerivation,
            WellknownMetadataKeys.MasterHDKey,
            stoppingToken).ConfigureAwait(false);

        if (signingKeyStr is null)
        {
            throw new InvalidOperationException("Wallet seed not available");
        }

        var signingKey = ExtKey.Parse(signingKeyStr, network.NBitcoinNetwork);
        var signingKeySettings = derivationScheme.GetAccountKeySettingsFromRoot(signingKey) ?? throw new InvalidOperationException("Wallet key settings not available");
        var rootedKeyPath = signingKeySettings.GetRootedKeyPath() ?? throw new InvalidOperationException("Wallet key path mismatch");
        var accountKey = signingKey.Derive(rootedKeyPath.KeyPath);
        var psbtToSign = proposal.PsbtToSign();
        var proposalPsbt = PSBT.Parse(psbtToSign, network.NBitcoinNetwork);

        var updated = await client.UpdatePSBTAsync(new NBXplorer.Models.UpdatePSBTRequest
        {
            PSBT = proposalPsbt,
            DerivationScheme = derivationScheme.AccountDerivation
        }, stoppingToken).ConfigureAwait(false);
        if (updated?.PSBT is not null)
        {
            proposalPsbt = updated.PSBT;
        }

        EnsureContributedInputsPresent(proposalPsbt, receiverCoins);
        derivationScheme.RebaseKeyPaths(proposalPsbt);
        proposalPsbt.RebaseKeyPaths(signingKeySettings.AccountKey, rootedKeyPath);
        proposalPsbt.SignAll(derivationScheme.AccountDerivation, accountKey, rootedKeyPath);
        FinalizeContributedInputs(proposalPsbt, receiverCoins);
        ClearSenderInputFinalization(proposalPsbt, receiverCoins);
        ClearPartialSignatures(proposalPsbt);

        foreach (var input in proposalPsbt.Inputs)
        {
            input.HDKeyPaths.Clear();
        }

        foreach (var output in proposalPsbt.Outputs)
        {
            output.HDKeyPaths.Clear();
        }

        using var transition = proposal.FinalizeProposal(new SigningProcessPsbt(proposalPsbt.ToBase64()));
        using var payjoinProposal = transition.Save(persister);
        await PostPayjoinProposalAsync(payjoinProposal, persister, ohttpRelayUrl, stoppingToken).ConfigureAwait(false);
    }

    internal static void EnsureContributedInputsPresent(PSBT proposalPsbt, ReceivedCoin[] receiverCoins)
    {
        var missingInputs = receiverCoins
            .Where(receiverCoin => proposalPsbt.Inputs.All(input => input.PrevOut != receiverCoin.OutPoint))
            .Select(receiverCoin => receiverCoin.OutPoint.ToString())
            .ToArray();

        if (missingInputs.Length == 0)
        {
            return;
        }

        throw new InvalidOperationException($"Provisional proposal is missing contributed receiver inputs: {string.Join(", ", missingInputs)}");
    }

    private static void FinalizeContributedInputs(PSBT proposalPsbt, ReceivedCoin[] receiverCoins)
    {
        // TODO: Collapse the receiver-input finalization and sender-input cleanup steps into a single proposal-normalization pipeline if this PSBT policy grows further.
        foreach (var input in proposalPsbt.Inputs)
        {
            if (!IsContributedReceiverInput(input.PrevOut, receiverCoins))
            {
                continue;
            }

            if (!input.TryFinalizeInput(out _))
            {
                throw new InvalidOperationException($"Receiver input '{input.PrevOut}' could not be finalized.");
            }
        }
    }

    internal static void ClearSenderInputFinalization(PSBT proposalPsbt, ReceivedCoin[] receiverCoins)
    {
        foreach (var input in proposalPsbt.Inputs)
        {
            if (IsContributedReceiverInput(input.PrevOut, receiverCoins))
            {
                continue;
            }

            input.FinalScriptSig = null;
            input.FinalScriptWitness = null;
        }
    }

    private static bool IsContributedReceiverInput(OutPoint prevOut, ReceivedCoin[] receiverCoins)
    {
        return receiverCoins.Any(receiverCoin => receiverCoin.OutPoint == prevOut);
    }

    internal static void ClearPartialSignatures(PSBT proposalPsbt)
    {
        foreach (var input in proposalPsbt.Inputs)
        {
            input.PartialSigs.Clear();
        }
    }

    private async Task PostPayjoinProposalAsync(
        PayjoinProposal proposal,
        JsonReceiverSessionPersister persister,
        SystemUri ohttpRelayUrl,
        CancellationToken stoppingToken)
    {
        using var requestResponse = proposal.CreatePostRequest(ohttpRelayUrl.ToString());
        var responseBody = await SendRelayRequestAsync(requestResponse.request, stoppingToken).ConfigureAwait(false);

        using var transition = proposal.ProcessResponse(responseBody, requestResponse.clientResponse);
        using var _ = transition.Save(persister);
    }

    private async Task<byte[]> SendRelayRequestAsync(Request request, CancellationToken stoppingToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, request.url)
        {
            Content = new ByteArrayContent(request.body)
        };
        message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.contentType);

        var client = _httpClientFactory.CreateClient(nameof(PayjoinReceiverPoller));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeoutCts.CancelAfter(RelayRequestTimeout);
        using var response = await client.SendAsync(message, timeoutCts.Token).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(timeoutCts.Token).ConfigureAwait(false);
    }

    private bool TryGetReceiverScript(PayjoinReceiverSessionState session, out byte[] script)
    {
        script = Array.Empty<byte>();
        var network = _networkProvider.GetNetwork<BTCPayNetwork>("BTC")?.NBitcoinNetwork;
        if (network is null)
        {
            return false;
        }

        try
        {
            var address = BitcoinAddress.Create(session.ReceiverAddress, network);
            script = address.ScriptPubKey.ToBytes();
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // TODO: Load all wallet-owned scripts from the store's derivation scheme and check the incoming script against that full set. 
    internal sealed class ReceiverScriptOwnedCallback : IsScriptOwned
    {
        private readonly byte[] _script;

        public ReceiverScriptOwnedCallback(byte[] script)
        {
            _script = script;
        }

        public bool Callback(byte[] script) => script.SequenceEqual(_script);
    }

    // TODO: Implement a persistent store of seen outpoints and check against it here.
    internal sealed class NoInputsSeenCallback : IsOutputKnown
    {
        public bool Callback(PlainOutPoint _outpoint) => false;
    }

    internal sealed class CloseRequestedBroadcastGuard : CanBroadcast
    {
        private readonly PayjoinReceiverSessionState _session;

        public CloseRequestedBroadcastGuard(PayjoinReceiverSessionState session)
        {
            _session = session;
        }

        public bool Callback(byte[] _tx) => !_session.IsCloseRequested;
    }

    internal sealed class SigningProcessPsbt : ProcessPsbt
    {
        private readonly string _signedPsbt;

        public SigningProcessPsbt(string signedPsbt)
        {
            _signedPsbt = signedPsbt;
        }

        public string Callback(string _psbt) => _signedPsbt;
    }
}
