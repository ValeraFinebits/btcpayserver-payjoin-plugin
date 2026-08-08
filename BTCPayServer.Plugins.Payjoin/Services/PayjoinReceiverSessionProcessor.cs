using BTCPayServer.Payments;
using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Services.Wallets;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Payjoin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal sealed class PayjoinReceiverSessionProcessor : IPayjoinReceiverSessionProcessor
{
    internal const int MaxConcurrentReceiverSessions = 8;

    private static readonly Action<ILogger, string, Exception?> LogPayjoinReceiverPollingFailedForInvoice =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(2, nameof(LogPayjoinReceiverPollingFailedForInvoice)),
            "Payjoin receiver polling failed for {InvoiceId}");
    private static readonly Action<ILogger, string, Exception?> LogPayjoinReceiverPreparedSettlementOutputs =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(6, nameof(LogPayjoinReceiverPreparedSettlementOutputs)),
            "Payjoin receiver prepared settlement outputs for {InvoiceId}");
    private static readonly Action<ILogger, string, Exception?> LogPayjoinReceiverSettlementOutputsUnavailable =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(7, nameof(LogPayjoinReceiverSettlementOutputsUnavailable)),
            "Payjoin receiver settlement outputs unavailable for {InvoiceId}");
    private static readonly Action<ILogger, string, string, Exception?> LogPayjoinReceiverSettlementOutputsFailed =
        LoggerMessage.Define<string, string>(LogLevel.Warning, new EventId(8, nameof(LogPayjoinReceiverSettlementOutputsFailed)),
            "Payjoin receiver failed to prepare settlement outputs for {InvoiceId}: {Message}");
    private static readonly Action<ILogger, string, string, Exception?> LogPayjoinReceiverSessionRemoved =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(9, nameof(LogPayjoinReceiverSessionRemoved)),
            "Payjoin receiver session removed for {InvoiceId}: {Reason}");
    private static readonly Action<ILogger, string, string, Exception?> LogPayjoinReceiverInputContributionFailed =
        LoggerMessage.Define<string, string>(LogLevel.Warning, new EventId(10, nameof(LogPayjoinReceiverInputContributionFailed)),
            "Payjoin receiver could not contribute inputs for {InvoiceId}: {Message}");
    private static readonly Action<ILogger, string, Exception?> LogPayjoinReceiverPersistedInputUnavailable =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(11, nameof(LogPayjoinReceiverPersistedInputUnavailable)),
            "Payjoin receiver persisted contributed input unavailable for {InvoiceId}");
    private static readonly Action<ILogger, string, double, Exception?> LogPayjoinReceiverInitializedPollTimedOut =
        LoggerMessage.Define<string, double>(LogLevel.Debug, new EventId(12, nameof(LogPayjoinReceiverInitializedPollTimedOut)),
            "Payjoin receiver initialized poll timed out for {InvoiceId} after {TimeoutSeconds} seconds; receiver session remains active.");
    private static readonly Action<ILogger, string, Exception?> LogPayjoinAccountingBridgeUnavailable =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(13, nameof(LogPayjoinAccountingBridgeUnavailable)),
            "Payjoin accounting bridge not found for {InvoiceId}");
    private static readonly Action<ILogger, string, Exception?> LogPayjoinReceiverFallbackTransactionUnavailable =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(14, nameof(LogPayjoinReceiverFallbackTransactionUnavailable)),
            "Payjoin receiver fallback transaction not yet available for {InvoiceId}");
    private static readonly Action<ILogger, string, Exception?> LogPayjoinReceiverFallbackNetworkUnavailable =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(15, nameof(LogPayjoinReceiverFallbackNetworkUnavailable)),
            "Payjoin receiver could not resolve fallback network for {InvoiceId}");
    private static readonly Action<ILogger, string, Exception?> LogPayjoinReceiverFallbackOutputUnavailable =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(16, nameof(LogPayjoinReceiverFallbackOutputUnavailable)),
            "Payjoin receiver could not resolve a unique fallback receiver output for {InvoiceId}");
    private static readonly Action<ILogger, string, Exception?> LogPayjoinReceiverFallbackOutputAmbiguous =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(17, nameof(LogPayjoinReceiverFallbackOutputAmbiguous)),
            "Payjoin receiver found multiple fallback outputs matching the receiver script for {InvoiceId}");
    private static readonly Action<ILogger, string, Exception?> LogPayjoinReceiverTxidUnstableSenderInput =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(18, nameof(LogPayjoinReceiverTxidUnstableSenderInput)),
            "Payjoin receiver session for {InvoiceId} was closed because the sender's proposal spends inputs that finalize through the scriptSig, so the final transaction id cannot be pinned for settlement tracking.");

    private readonly PayjoinReceiverSessionStore _sessionStore;
    private readonly IPayjoinReceiverSessionGuard _sessionGuard;
    private readonly IPayjoinReceiverStateProcessor _stateProcessor;
    private readonly IPayjoinReceiverOutputBuilder _outputBuilder;
    private readonly IPayjoinReceiverInputSelector _inputSelector;
    private readonly IPayjoinAccountingBridgeService _accountingBridgeService;
    private readonly IPayjoinAccountingPaymentService _accountingPaymentService;
    private readonly IPayjoinInvoiceLookup _invoiceLookup;
    private readonly IPayjoinReceiverProposalFinalizer _proposalFinalizer;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly ILogger<PayjoinReceiverSessionProcessor> _logger;

    public PayjoinReceiverSessionProcessor(
        PayjoinReceiverSessionStore sessionStore,
        IPayjoinReceiverSessionGuard sessionGuard,
        IPayjoinReceiverStateProcessor stateProcessor,
        IPayjoinReceiverOutputBuilder outputBuilder,
        IPayjoinReceiverInputSelector inputSelector,
        IPayjoinAccountingBridgeService accountingBridgeService,
        IPayjoinAccountingPaymentService accountingPaymentService,
        IPayjoinInvoiceLookup invoiceLookup,
        IPayjoinReceiverProposalFinalizer proposalFinalizer,
        BTCPayNetworkProvider networkProvider,
        ILogger<PayjoinReceiverSessionProcessor> logger)
    {
        _sessionStore = sessionStore;
        _sessionGuard = sessionGuard;
        _stateProcessor = stateProcessor;
        _outputBuilder = outputBuilder;
        _inputSelector = inputSelector;
        _accountingBridgeService = accountingBridgeService;
        _accountingPaymentService = accountingPaymentService;
        _invoiceLookup = invoiceLookup;
        _proposalFinalizer = proposalFinalizer;
        _networkProvider = networkProvider;
        _logger = logger;
    }

    public async Task ProcessTickAsync(CancellationToken stoppingToken)
    {
        await Parallel.ForEachAsync(
            _sessionStore.GetSessions(),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxConcurrentReceiverSessions,
                CancellationToken = stoppingToken
            },
            async (session, cancellationToken) =>
            {
                await ProcessSessionWithIsolationAsync(session, cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    private async ValueTask ProcessSessionWithIsolationAsync(PayjoinReceiverSessionState session, CancellationToken stoppingToken)
    {
        try
        {
            await ProcessSessionAsync(session, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
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
    }

    private async Task ProcessSessionAsync(PayjoinReceiverSessionState session, CancellationToken stoppingToken)
    {
        using var guardedSession = await _sessionGuard.TryPrepareAsync(session, stoppingToken).ConfigureAwait(false);
        if (guardedSession is null)
        {
            return;
        }

        session = guardedSession.Session;
        var persister = guardedSession.Persister;
        var receiverScript = guardedSession.ReceiverScript;
        var stateContext = guardedSession.StateContext;
        await TryAttachFallbackAsync(session, receiverScript, guardedSession.Replay, stoppingToken).ConfigureAwait(false);

        switch (guardedSession.State)
        {
            case ReceiveSession.Initialized initialized:
                try
                {
                    await _stateProcessor.ProcessInitializedAsync(stateContext, initialized.Inner, ContinueWithOutputsAsync, stoppingToken).ConfigureAwait(false);
                }
                catch (PayjoinReceiverRelayTimeoutException ex)
                {
                    LogPayjoinReceiverInitializedPollTimedOut(_logger, session.InvoiceId, ex.Timeout.TotalSeconds, null);
                }

                break;
            case ReceiveSession.HasReplyableError hasReplyableError:
                await _stateProcessor.ProcessReplyableErrorAsync(stateContext, hasReplyableError.Inner, stoppingToken).ConfigureAwait(false);
                break;
            case ReceiveSession.UncheckedOriginalPayload payload:
                await _stateProcessor.ProcessUncheckedProposalAsync(stateContext, payload.Inner, ContinueWithOutputsAsync, stoppingToken).ConfigureAwait(false);
                break;
            case ReceiveSession.MaybeInputsOwned maybeInputsOwned:
                await _stateProcessor.ProcessMaybeInputsOwnedAsync(stateContext, maybeInputsOwned.Inner, ContinueWithOutputsAsync, stoppingToken).ConfigureAwait(false);
                break;
            case ReceiveSession.MaybeInputsSeen maybeInputsSeen:
                await _stateProcessor.ProcessMaybeInputsSeenAsync(stateContext, maybeInputsSeen.Inner, ContinueWithOutputsAsync, stoppingToken).ConfigureAwait(false);
                break;
            case ReceiveSession.OutputsUnknown outputsUnknown:
                await _stateProcessor.ProcessOutputsUnknownAsync(stateContext, outputsUnknown.Inner, ContinueWithOutputsAsync, stoppingToken).ConfigureAwait(false);
                break;
            case ReceiveSession.WantsOutputs wantsOutputs:
                await ContinueWithOutputsAsync(wantsOutputs.Inner, stateContext, stoppingToken).ConfigureAwait(false);
                break;
            case ReceiveSession.WantsInputs wantsInputs:
                await ProcessWantsInputsAsync(wantsInputs.Inner, persister, receiverScript, session.StoreId, session.InvoiceId, GetReservationExpiresAt(session), stoppingToken).ConfigureAwait(false);
                break;
            case ReceiveSession.WantsFeeRange wantsFeeRange:
                {
                    var contributedCoinsForFeeRange = await TryGetRequiredPersistedContributedCoinsAsync(session, "persisted receiver input unavailable", stoppingToken).ConfigureAwait(false);
                    if (contributedCoinsForFeeRange is null)
                    {
                        return;
                    }

                    await _proposalFinalizer.FinalizeAsync(
                        new PayjoinReceiverProposalFinalizationContext(persister, session.StoreId, session.InvoiceId, PayjoinConstants.BitcoinCode),
                        wantsFeeRange.Inner,
                        contributedCoinsForFeeRange,
                        stoppingToken).ConfigureAwait(false);
                    break;
                }
            case ReceiveSession.ProvisionalProposal provisionalProposal:
                {
                    var contributedCoinsForProposal = await TryGetRequiredPersistedContributedCoinsAsync(session, "persisted receiver input unavailable", stoppingToken).ConfigureAwait(false);
                    if (contributedCoinsForProposal is null)
                    {
                        return;
                    }

                    await _proposalFinalizer.FinalizeAsync(
                        new PayjoinReceiverProposalFinalizationContext(persister, session.StoreId, session.InvoiceId, PayjoinConstants.BitcoinCode),
                        provisionalProposal.Inner,
                        contributedCoinsForProposal,
                        stoppingToken).ConfigureAwait(false);
                    break;
                }
            case ReceiveSession.PayjoinProposal payjoinProposal:
                {
                    // A replay can land here when the previous attempt stopped between finalizing the
                    // proposal and recording its expected transaction on the accounting bridge, so the
                    // recording is completed before the proposal is handed to the sender again.
                    var finalizationContext = new PayjoinReceiverProposalFinalizationContext(persister, session.StoreId, session.InvoiceId, PayjoinConstants.BitcoinCode);
                    await _proposalFinalizer.EnsureExpectedFinalTransactionAsync(finalizationContext, payjoinProposal.Inner, stoppingToken).ConfigureAwait(false);
                    await _proposalFinalizer.PostAsync(finalizationContext, payjoinProposal.Inner, stoppingToken).ConfigureAwait(false);
                    break;
                }
            case ReceiveSession.Monitor:
                break;
            case ReceiveSession.Closed:
                RemoveSession(session.InvoiceId, "receiver session closed");
                break;
        }
    }

    private Task ContinueWithOutputsAsync(
        WantsOutputs proposal,
        PayjoinReceiverStateContext context,
        CancellationToken cancellationToken)
    {
        return ProcessWantsOutputsAsync(
            proposal,
            context.Persister,
            context.ReceiverScript,
            context.StoreId,
            context.InvoiceId,
            GetReservationExpiresAt(context.Session),
            cancellationToken);
    }

    private async Task ProcessWantsOutputsAsync(
        WantsOutputs proposal,
        JsonReceiverSessionPersister persister,
        byte[] receiverScript,
        string storeId,
        string invoiceId,
        DateTimeOffset reservationExpiresAt,
        CancellationToken stoppingToken)
    {
        var settlementOutputs = await CreateSettlementOutputsOrRemoveSessionAsync(proposal, storeId, invoiceId, receiverScript, stoppingToken).ConfigureAwait(false);
        if (settlementOutputs is null)
        {
            return;
        }

        try
        {
            using var wantsInputs = ApplySettlementOutputs(proposal, invoiceId, settlementOutputs);
            LogPayjoinReceiverPreparedSettlementOutputs(_logger, invoiceId, null);
            await ProcessWantsInputsAsync(wantsInputs, persister, receiverScript, storeId, invoiceId, reservationExpiresAt, stoppingToken).ConfigureAwait(false);
        }
        catch (OutputSubstitutionException ex)
        {
            LogPayjoinReceiverSettlementOutputsFailed(_logger, invoiceId, ex.Message, ex);
            RemoveSession(invoiceId, "failed to prepare settlement outputs");
        }
    }

    private async Task ProcessWantsInputsAsync(
        WantsInputs proposal,
        JsonReceiverSessionPersister persister,
        byte[] receiverScript,
        string storeId,
        string invoiceId,
        DateTimeOffset reservationExpiresAt,
        CancellationToken stoppingToken)
    {
        var contributionResult = await TryCreateInputContributionAsync(proposal, storeId, invoiceId, reservationExpiresAt, stoppingToken).ConfigureAwait(false);
        if (contributionResult is null)
        {
            return;
        }

        var contribution = contributionResult.Value;

        try
        {
            await FinalizeInputContributionAsync(contribution, persister, storeId, invoiceId, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            contribution.ProposalWithInputs!.Dispose();
        }
    }

    private async Task<PayjoinReceiverOutputBuilder.OutputReplacement?> CreateSettlementOutputsOrRemoveSessionAsync(
        WantsOutputs proposal,
        string storeId,
        string invoiceId,
        byte[] receiverScript,
        CancellationToken stoppingToken)
    {
        var preserveReceiverScript = proposal.OutputSubstitution() == OutputSubstitution.Disabled;
        var bridge = await _accountingBridgeService.TryGetByInvoiceIdAsync(invoiceId, stoppingToken).ConfigureAwait(false);
        var settlementOutputs = await _outputBuilder.TryCreateSettlementOutputsAsync(
            storeId,
            invoiceId,
            receiverScript,
            preserveReceiverScript,
            bridge?.EffectiveInvoiceValueSats,
            stoppingToken).ConfigureAwait(false);
        if (settlementOutputs is not null)
        {
            return settlementOutputs;
        }

        LogPayjoinReceiverSettlementOutputsUnavailable(_logger, invoiceId, null);
        RemoveSession(invoiceId, "settlement outputs unavailable");
        return null;
    }

    private WantsInputs ApplySettlementOutputs(WantsOutputs proposal, string invoiceId, PayjoinReceiverOutputBuilder.OutputReplacement settlementOutputs)
    {
        using var modified = proposal.ReplaceReceiverOutputs(settlementOutputs.ReplacementOutputs, settlementOutputs.SettlementScript);
        using var transition = modified.CommitOutputs();

        // The committed-outputs event and the settlement script/amount the accounting side needs to
        // recognize and credit that settlement are written in one database transaction, so a restart
        // cannot leave the session advanced past output commitment with the accounting record missing
        // the script it must look for.
        var capturingPersister = new CapturingReceiverSessionPersister();
        var wantsInputs = transition.Save(capturingPersister);
        try
        {
            PersistCommittedOutputs(invoiceId, capturingPersister.Events, settlementOutputs);
            return wantsInputs;
        }
        catch
        {
            // Nothing was persisted, so the next tick replays back into WantsOutputs and rebuilds.
            wantsInputs.Dispose();
            throw;
        }
    }

    internal void PersistCommittedOutputs(
        string invoiceId,
        IReadOnlyList<string> events,
        PayjoinReceiverOutputBuilder.OutputReplacement settlementOutputs)
    {
        var settlementScriptHex = Convert.ToHexString(settlementOutputs.SettlementScript);
        var settlementAmountSats = checked((long)settlementOutputs.SettlementAmountSats);
        _sessionStore.AppendEventsWithAccountingUpdate(
            invoiceId,
            events,
            bridge =>
            {
                bridge.SettlementScript = settlementScriptHex;
                bridge.EffectiveInvoiceValueSats = settlementAmountSats;
            });
    }

    private async Task<ReceiverInputContribution?> TryCreateInputContributionAsync(
        WantsInputs proposal,
        string storeId,
        string invoiceId,
        DateTimeOffset reservationExpiresAt,
        CancellationToken stoppingToken)
    {
        var contribution = await _inputSelector.TryContributeInputsAsync(proposal, storeId, invoiceId, reservationExpiresAt, stoppingToken).ConfigureAwait(false);
        if (contribution.ProposalWithInputs is not null && contribution.ContributedCoins is not null)
        {
            return new ReceiverInputContribution(contribution.ProposalWithInputs, contribution.ContributedCoins);
        }

        // An exhausted contribution attempt ends the session deliberately. Keeping it alive to
        // retry on later ticks looks tempting for transient contention, but those races already
        // resolve inside the attempt, and a session that lingers after its sender gave up would
        // grab and reserve freshly confirmed coins until the monitoring deadline, starving future
        // payjoins - the concurrency integration tests assert exactly this. Ending the session is
        // the async analog of the v1 endpoint answering that payjoin is unavailable: the sender
        // falls back and their original transaction still pays the invoice.
        LogPayjoinReceiverInputContributionFailed(_logger, invoiceId, contribution.FailureMessage, null);
        RemoveSession(invoiceId, "no receiver input available for payjoin contribution");
        return null;
    }

    private readonly record struct ReceiverInputContribution(WantsInputs ProposalWithInputs, ReceivedCoin[] ContributedCoins);

    private async Task FinalizeInputContributionAsync(
        ReceiverInputContribution contribution,
        JsonReceiverSessionPersister persister,
        string storeId,
        string invoiceId,
        CancellationToken stoppingToken)
    {
        using var transition = contribution.ProposalWithInputs!.CommitInputs();
        using var wantsFeeRange = transition.Save(persister);
        await _proposalFinalizer.FinalizeAsync(
            new PayjoinReceiverProposalFinalizationContext(persister, storeId, invoiceId, PayjoinConstants.BitcoinCode),
            wantsFeeRange,
            contribution.ContributedCoins,
            stoppingToken).ConfigureAwait(false);
    }

    private async Task<ReceivedCoin[]?> TryGetRequiredPersistedContributedCoinsAsync(
        PayjoinReceiverSessionState session,
        string removalReason,
        CancellationToken cancellationToken)
    {
        var contributedCoins = await _inputSelector.TryGetPersistedContributedCoinsAsync(session, cancellationToken).ConfigureAwait(false);
        if (contributedCoins is not null)
        {
            return contributedCoins;
        }

        LogPayjoinReceiverPersistedInputUnavailable(_logger, session.InvoiceId, null);
        RemoveSession(session.InvoiceId, removalReason);
        return null;
    }

    private static DateTimeOffset GetReservationExpiresAt(PayjoinReceiverSessionState session)
    {
        // TODO: Revisit whether receiver-input reservations should always live until MonitoringExpiresAt or use a shorter policy-derived lifetime.
        return session.MonitoringExpiresAt;
    }

    private async Task TryAttachFallbackAsync(PayjoinReceiverSessionState session, byte[] receiverScript, ReplayResult replay, CancellationToken cancellationToken)
    {
        var bridge = await _accountingBridgeService.TryGetByInvoiceIdAsync(session.InvoiceId, cancellationToken).ConfigureAwait(false);
        if (bridge is null)
        {
            LogPayjoinAccountingBridgeUnavailable(_logger, session.InvoiceId, null);
            return;
        }

        if (bridge.FallbackTransactionId is not null)
        {
            return;
        }

        using var history = replay.SessionHistory();
        var fallbackBytes = history.FallbackTx();
        if (fallbackBytes is null || fallbackBytes.Length == 0)
        {
            LogPayjoinReceiverFallbackTransactionUnavailable(_logger, session.InvoiceId, null);
            return;
        }

        var network = _networkProvider.GetNetwork<BTCPayNetwork>(bridge.CryptoCode)?.NBitcoinNetwork;
        if (network is null)
        {
            LogPayjoinReceiverFallbackNetworkUnavailable(_logger, session.InvoiceId, null);
            return;
        }

        var fallbackTx = Transaction.Load(fallbackBytes, network);
        var fallbackOutputMatch = ResolveFallbackReceiverOutput(fallbackTx, receiverScript);
        if (!fallbackOutputMatch.Success)
        {
            if (fallbackOutputMatch.Status == FallbackReceiverOutputMatchStatus.Ambiguous)
            {
                LogPayjoinReceiverFallbackOutputAmbiguous(_logger, session.InvoiceId, null);
            }
            else
            {
                LogPayjoinReceiverFallbackOutputUnavailable(_logger, session.InvoiceId, null);
            }

            return;
        }

        var outputIndex = fallbackOutputMatch.OutputIndex!.Value;
        var valueSats = fallbackOutputMatch.ValueSats!.Value;
        var invoice = await _invoiceLookup.GetInvoiceAsync(session.InvoiceId).ConfigureAwait(false);
        var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(PayjoinConstants.BitcoinCode);
        var effectiveInvoiceValueSats = invoice?.GetPaymentPrompt(paymentMethodId)?.Calculate().Due is { } due && due > 0m
            ? Money.Coins(due).Satoshi
            : valueSats;
        var updatedBridge = await _accountingBridgeService.AttachFallbackAsync(
            session.InvoiceId,
            fallbackTx.GetHash().ToString(),
            outputIndex,
            valueSats,
            effectiveInvoiceValueSats,
            null,
            cancellationToken).ConfigureAwait(false);
    }

    internal static FallbackReceiverOutputMatch ResolveFallbackReceiverOutput(Transaction fallbackTransaction, byte[] receiverScript)
    {
        var receiverScriptPubKey = Script.FromBytesUnsafe(receiverScript);
        var matches = fallbackTransaction.Outputs
            .Select((output, index) => new { output, index })
            .Where(x => x.output.ScriptPubKey == receiverScriptPubKey)
            .ToArray();

        return matches.Length switch
        {
            1 => FallbackReceiverOutputMatch.FoundMatch(checked((uint)matches[0].index), matches[0].output.Value.Satoshi),
            0 => FallbackReceiverOutputMatch.NotFound(),
            _ => FallbackReceiverOutputMatch.Ambiguous()
        };
    }

    internal readonly record struct FallbackReceiverOutputMatch(uint? OutputIndex, long? ValueSats, FallbackReceiverOutputMatchStatus Status)
    {
        internal bool Success => Status == FallbackReceiverOutputMatchStatus.Found;

        internal static FallbackReceiverOutputMatch FoundMatch(uint outputIndex, long valueSats)
        {
            return new FallbackReceiverOutputMatch(outputIndex, valueSats, FallbackReceiverOutputMatchStatus.Found);
        }

        internal static FallbackReceiverOutputMatch NotFound()
        {
            return new FallbackReceiverOutputMatch(null, null, FallbackReceiverOutputMatchStatus.NotFound);
        }

        internal static FallbackReceiverOutputMatch Ambiguous()
        {
            return new FallbackReceiverOutputMatch(null, null, FallbackReceiverOutputMatchStatus.Ambiguous);
        }
    }

    internal enum FallbackReceiverOutputMatchStatus
    {
        Found,
        NotFound,
        Ambiguous
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

}
