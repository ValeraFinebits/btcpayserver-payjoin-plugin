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
        if (!await RefreshInvoiceCloseStateAsync(session).ConfigureAwait(false))
        {
            return;
        }

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

        var persister = PayjoinReceiverSessionStore.CreatePersister(session);
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

        if (session.IsCloseRequested)
        {
            // TODO: Send a persisted replyable receiver rejection here after the payjoin library supports closing an in-flight session cleanly.
            RemoveSession(session.InvoiceId, $"invoice is no longer payable ({session.CloseInvoiceStatus})");
            return;
        }

        switch (state)
        {
            case ReceiveSession.Initialized initialized:
                await PollInitializedAsync(initialized.inner, persister, receiverScript, session.OhttpRelayUrl, session.StoreId, session.InvoiceId, stoppingToken).ConfigureAwait(false);
                break;
            case ReceiveSession.HasReplyableError hasReplyableError:
                await ProcessReplyableErrorAsync(hasReplyableError.inner, persister, session.OhttpRelayUrl, stoppingToken).ConfigureAwait(false);
                break;
            case ReceiveSession.UncheckedOriginalPayload payload:
                await ProcessUncheckedProposalAsync(payload.inner, persister, receiverScript, session.OhttpRelayUrl, session.StoreId, session.InvoiceId, stoppingToken).ConfigureAwait(false);
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
                await ProcessWantsOutputsAsync(session, wantsOutputs.inner, persister, receiverScript, session.OhttpRelayUrl, session.StoreId, session.InvoiceId, stoppingToken).ConfigureAwait(false);
                break;
            case ReceiveSession.WantsInputs wantsInputs:
                await ProcessWantsInputsAsync(session, wantsInputs.inner, persister, receiverScript, session.OhttpRelayUrl, session.StoreId, session.InvoiceId, stoppingToken).ConfigureAwait(false);
                break;
            case ReceiveSession.WantsFeeRange wantsFeeRange:
                await ProcessWantsFeeRangeAsync(wantsFeeRange.inner, persister, receiverScript, session.OhttpRelayUrl, session.StoreId, session.InvoiceId, GetRequiredContributedInputs(session, nameof(WantsFeeRange)), stoppingToken).ConfigureAwait(false);
                break;
            case ReceiveSession.ProvisionalProposal provisionalProposal:
                await ProcessProvisionalProposalAsync(provisionalProposal.inner, persister, receiverScript, session.OhttpRelayUrl, session.StoreId, session.InvoiceId, GetRequiredContributedInputs(session, nameof(ProvisionalProposal)), stoppingToken).ConfigureAwait(false);
                break;
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

    private async Task<bool> RefreshInvoiceCloseStateAsync(PayjoinReceiverSessionState session)
    {
        var invoice = await _invoiceRepository.GetInvoice(session.InvoiceId).ConfigureAwait(false);
        if (invoice is null)
        {
            RemoveSession(session.InvoiceId, "invoice no longer exists");
            return false;
        }

        if (invoice.GetInvoiceState().Status != InvoiceStatus.New)
        {
            session.RequestClose(invoice.GetInvoiceState().Status);
        }

        return true;
    }

    private bool TryExpireSession(PayjoinReceiverSessionState session)
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

    private async Task PollInitializedAsync(
        Initialized initialized,
        JsonReceiverSessionPersister persister,
        byte[] receiverScript,
        SystemUri ohttpRelayUrl,
        string storeId,
        string invoiceId,
        CancellationToken stoppingToken)
    {
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
            await ProcessUncheckedProposalAsync(progress.inner, persister, receiverScript, ohttpRelayUrl, storeId, invoiceId, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessUncheckedProposalAsync(
        UncheckedOriginalPayload proposal,
        JsonReceiverSessionPersister persister,
        byte[] receiverScript,
        SystemUri ohttpRelayUrl,
        string storeId,
        string invoiceId,
        CancellationToken stoppingToken)
    {
        using var transition = proposal.AssumeInteractiveReceiver();
        using var maybeInputsOwned = transition.Save(persister);
        await ProcessMaybeInputsOwnedAsync(maybeInputsOwned, persister, receiverScript, ohttpRelayUrl, storeId, invoiceId, stoppingToken).ConfigureAwait(false);
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
        await ProcessWantsOutputsAsync(GetRequiredSessionState(invoiceId), wantsOutputs, persister, receiverScript, ohttpRelayUrl, storeId, invoiceId, stoppingToken).ConfigureAwait(false);
    }

    private async Task ProcessWantsOutputsAsync(
        PayjoinReceiverSessionState session,
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
            await ProcessWantsInputsAsync(session, wantsInputs, persister, receiverScript, ohttpRelayUrl, storeId, invoiceId, stoppingToken).ConfigureAwait(false);
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

    private static (PlainTxOut[] ExactPaymentOutputs, byte[] ReceiverChangeScript) CreateExactPaymentReceiverOutputs(
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
        PayjoinReceiverSessionState session,
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
        PayjoinReceiverContributedInput[]? contributedInputs = null;
        var contributionFailures = new List<string>();
        try
        {
            // TODO: Restore `proposal.TryPreservingPrivacy(receiverInputs)` only after rust-payjoin/payjoin-ffi lets us identify which `ReceivedCoin` was actually selected.
            // TODO: We must persist only the truly contributed coin(s) into `contributedInputs`; otherwise the signing step can treat unrelated wallet inputs as receiver-owned and produce an invalid proposal.
            var orderedCandidates = receiverCoins
                .Select((coin, index) => new { coin, index })
                .OrderBy(x => x.coin.Coin.Amount.Satoshi)
                .Select(x => x.index);

            foreach (var index in orderedCandidates)
            {
                try
                {
                    withInputs = proposal.ContributeInputs(new[] { receiverInputs[index] });
                    contributedInputs = new[] { CreateContributedInput(receiverCoins[index]) };
                    break;
                }
                catch (InputContributionException ex)
                {
                    contributionFailures.Add($"candidate '{receiverCoins[index].OutPoint}' rejected: {ex.Message}");
                }
            }

            if (withInputs is null || contributedInputs is null)
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

            session.SetContributedInputs(contributedInputs);
            using var transition = withInputs.CommitInputs();
            using var wantsFeeRange = transition.Save(persister);
            await ProcessWantsFeeRangeAsync(wantsFeeRange, persister, receiverScript, ohttpRelayUrl, storeId, invoiceId, contributedInputs, stoppingToken).ConfigureAwait(false);
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
        PayjoinReceiverContributedInput[] receiverInputs,
        CancellationToken stoppingToken)
    {
        // TODO: Replace hardcoded fee range with values from NBXplorer fee estimation.
        using var transition = proposal.ApplyFeeRange(1, 10);
        using var provisional = transition.Save(persister);
        await ProcessProvisionalProposalAsync(provisional, persister, receiverScript, ohttpRelayUrl, storeId, invoiceId, receiverInputs, stoppingToken).ConfigureAwait(false);
    }

    private async Task ProcessProvisionalProposalAsync(
        ProvisionalProposal proposal,
        JsonReceiverSessionPersister persister,
        byte[] receiverScript,
        SystemUri ohttpRelayUrl,
        string storeId,
        string invoiceId,
        PayjoinReceiverContributedInput[] receiverInputs,
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

        EnsureContributedInputsPresent(proposalPsbt, receiverInputs);
        derivationScheme.RebaseKeyPaths(proposalPsbt);
        proposalPsbt.RebaseKeyPaths(signingKeySettings.AccountKey, rootedKeyPath);
        proposalPsbt.SignAll(derivationScheme.AccountDerivation, accountKey, rootedKeyPath);
        FinalizeContributedInputs(proposalPsbt, receiverInputs);
        ClearSenderInputFinalization(proposalPsbt, receiverInputs);
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

    private static PayjoinReceiverContributedInput[] GetRequiredContributedInputs(PayjoinReceiverSessionState session, string stateName)
    {
        var contributedInputs = session.GetContributedInputs();
        if (contributedInputs.Length == 0)
        {
            throw new InvalidOperationException($"Persisted contributed receiver inputs are missing for replayed {stateName} state.");
        }

        return contributedInputs;
    }

    private PayjoinReceiverSessionState GetRequiredSessionState(string invoiceId)
    {
        if (_sessionStore.TryGetSession(invoiceId, out var session) && session is not null)
        {
            return session;
        }

        throw new InvalidOperationException($"Receiver session '{invoiceId}' is no longer available.");
    }

    private static PayjoinReceiverContributedInput CreateContributedInput(ReceivedCoin coin)
    {
        return new PayjoinReceiverContributedInput(coin.OutPoint, coin.KeyPath);
    }

    private static void EnsureContributedInputsPresent(PSBT proposalPsbt, PayjoinReceiverContributedInput[] receiverInputs)
    {
        var missingInputs = receiverInputs
            .Where(receiverInput => proposalPsbt.Inputs.All(input => input.PrevOut != receiverInput.OutPoint))
            .Select(receiverInput => receiverInput.OutPoint.ToString())
            .ToArray();

        if (missingInputs.Length == 0)
        {
            return;
        }

        throw new InvalidOperationException($"Provisional proposal is missing contributed receiver inputs: {string.Join(", ", missingInputs)}");
    }

    private static void FinalizeContributedInputs(PSBT proposalPsbt, PayjoinReceiverContributedInput[] receiverInputs)
    {
        // TODO: Collapse the receiver-input finalization and sender-input cleanup steps into a single proposal-normalization pipeline if this PSBT policy grows further.
        foreach (var input in proposalPsbt.Inputs)
        {
            if (!receiverInputs.Any(receiverInput => receiverInput.OutPoint == input.PrevOut))
            {
                continue;
            }

            if (!input.TryFinalizeInput(out _))
            {
                throw new InvalidOperationException($"Receiver input '{input.PrevOut}' could not be finalized.");
            }
        }
    }

    private static void ClearSenderInputFinalization(PSBT proposalPsbt, PayjoinReceiverContributedInput[] receiverInputs)
    {
        foreach (var input in proposalPsbt.Inputs)
        {
            if (receiverInputs.Any(receiverInput => receiverInput.OutPoint == input.PrevOut))
            {
                continue;
            }

            input.FinalScriptSig = null;
            input.FinalScriptWitness = null;
        }
    }

    private static void ClearPartialSignatures(PSBT proposalPsbt)
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
    private sealed class ReceiverScriptOwnedCallback : IsScriptOwned
    {
        private readonly byte[] _script;

        public ReceiverScriptOwnedCallback(byte[] script)
        {
            _script = script;
        }

        public bool Callback(byte[] script) => script.SequenceEqual(_script);
    }

    // TODO: Implement a persistent store of seen outpoints and check against it here.
    private sealed class NoInputsSeenCallback : IsOutputKnown
    {
        public bool Callback(PlainOutPoint _outpoint) => false;
    }

    private sealed class SigningProcessPsbt : ProcessPsbt
    {
        private readonly string _signedPsbt;

        public SigningProcessPsbt(string signedPsbt)
        {
            _signedPsbt = signedPsbt;
        }

        public string Callback(string _psbt) => _signedPsbt;
    }
}
