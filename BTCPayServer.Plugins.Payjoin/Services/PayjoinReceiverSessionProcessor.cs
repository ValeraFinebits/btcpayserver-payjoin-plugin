using ReceiverAttemptOwner = BTCPayServer.Plugins.Payjoin.Services.PayjoinReceiverSessionStore.DatabaseBackedReceiverPersister;
using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Services.Wallets;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Payjoin;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal sealed class PayjoinReceiverSessionProcessor : IPayjoinReceiverSessionProcessor
{
    internal const int MaxConcurrentReceiverSessions = 16;

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
    private static readonly Action<ILogger, string, Exception?> LogPayjoinReceiverTxidUnstableSenderInput =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(18, nameof(LogPayjoinReceiverTxidUnstableSenderInput)),
            "Payjoin receiver session for {InvoiceId} was closed because the sender's proposal spends inputs that finalize through the scriptSig, so the final transaction id cannot be pinned for settlement tracking.");

    private static readonly Action<ILogger, string, string, Exception?> LogPayjoinReceiverAmountRejected =
        LoggerMessage.Define<string, string>(LogLevel.Warning, new EventId(19, nameof(LogPayjoinReceiverAmountRejected)),
            "Payjoin receiver stopped for {InvoiceId}: {Reason}.");

    private readonly PayjoinReceiverSessionStore _sessionStore;
    private readonly IPayjoinReceiverSessionGuard _sessionGuard;
    private readonly IPayjoinReceiverStateProcessor _stateProcessor;
    private readonly IPayjoinReceiverOutputBuilder _outputBuilder;
    private readonly IPayjoinReceiverInputSelector _inputSelector;
    private readonly IPayjoinAccountingBridgeService _accountingBridgeService;
    private readonly IPayjoinReceiverProposalFinalizer _proposalFinalizer;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly ILogger<PayjoinReceiverSessionProcessor> _logger;
    private readonly IPayjoinOriginalTransactionValidator _originalValidator;

    public PayjoinReceiverSessionProcessor(
        PayjoinReceiverSessionStore sessionStore,
        IPayjoinReceiverSessionGuard sessionGuard,
        IPayjoinReceiverStateProcessor stateProcessor,
        IPayjoinReceiverOutputBuilder outputBuilder,
        IPayjoinReceiverInputSelector inputSelector,
        IPayjoinAccountingBridgeService accountingBridgeService,
        IPayjoinReceiverProposalFinalizer proposalFinalizer,
        BTCPayNetworkProvider networkProvider,
        ILogger<PayjoinReceiverSessionProcessor> logger,
        IPayjoinOriginalTransactionValidator originalValidator)
    {
        _sessionStore = sessionStore;
        _sessionGuard = sessionGuard;
        _stateProcessor = stateProcessor;
        _outputBuilder = outputBuilder;
        _inputSelector = inputSelector;
        _accountingBridgeService = accountingBridgeService;
        _proposalFinalizer = proposalFinalizer;
        _networkProvider = networkProvider;
        _logger = logger;
        _originalValidator = originalValidator;
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

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "This is the per-session isolation boundary of a background poller: one session's failure must never cancel the parallel tick or crash the host.")]
    private async ValueTask ProcessSessionWithIsolationAsync(PayjoinReceiverSessionState session, CancellationToken stoppingToken)
    {
        try
        {
            try
            {
                await ProcessSessionAsync(session, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (PayjoinSessionStaleException.IsStale(ex))
            {
                (ex as IDisposable)?.Dispose();
                // A replaced/advanced attempt owns no cleanup or failure transition.
            }
            catch (InvalidOperationException ex)
            {
                LogPayjoinReceiverPollingFailedForInvoice(_logger, session.InvoiceId, ex);
                // An unclassified load/write failure is not evidence that history can be discarded.
                // A fresh pass must load and replay the actual committed state.
            }
            catch (ReceiverPersistedException ex)
            {
                LogPayjoinReceiverPollingFailedForInvoice(_logger, session.InvoiceId, ex);
            }
            catch (UniffiException ex)
            {
                LogPayjoinReceiverPollingFailedForInvoice(_logger, session.InvoiceId, ex);
                // TODO: Handle Storage errors without deleting the session; recover by replaying its event log.
                // https://github.com/ValeraFinebits/btcpayserver-payjoin-plugin/issues/198
                // Preserve the current attempt on a technical/native storage or POST failure.
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogPayjoinReceiverPollingFailedForInvoice(_logger, session.InvoiceId, ex);
        }
    }

    private async Task ProcessSessionAsync(PayjoinReceiverSessionState session, CancellationToken stoppingToken)
    {
        _sessionStore.GetAttempt(session.InvoiceId, session.SessionInstanceId);
        // Failed sessions are recovery evidence. The guard may otherwise expire or close them.
        var bridge = await _accountingBridgeService.TryGetByInvoiceIdAsync(session.InvoiceId, stoppingToken).ConfigureAwait(false);
        if (bridge?.Status == PayjoinAccountingBridgeStatus.Failed)
        {
            return;
        }

        using var guardedSession = await _sessionGuard.TryPrepareAsync(session, stoppingToken).ConfigureAwait(false);
        if (guardedSession is null)
        {
            return;
        }

        session = guardedSession.Session;
        var persister = guardedSession.Persister;
        var receiverScript = guardedSession.ReceiverScript;
        var stateContext = guardedSession.StateContext;
        try
        {
            if (guardedSession.State is ReceiveSession.PayjoinProposal prepared)
            {
                if (ValidateAndAttachOriginalPayment(persister, receiverScript) is null) return;
                await _proposalFinalizer.EnsureExpectedFinalTransactionAsync(
                    new PayjoinReceiverProposalFinalizationContext(persister, session.StoreId, session.InvoiceId, PayjoinConstants.BitcoinCode),
                    prepared.Inner, stoppingToken).ConfigureAwait(false);
            }

            if (guardedSession.State is ReceiveSession.MaybeInputsOwned or ReceiveSession.MaybeInputsSeen or
                ReceiveSession.OutputsUnknown or ReceiveSession.WantsOutputs or ReceiveSession.WantsInputs or
                ReceiveSession.WantsFeeRange or ReceiveSession.ProvisionalProposal or ReceiveSession.PayjoinProposal)
            {
                using var history = guardedSession.Replay.SessionHistory();
                var original = history.FallbackTx();
                var validation = original is { Length: > 0 }
                    ? await _originalValidator.ValidateAsync(original, stoppingToken).ConfigureAwait(false)
                    : new OriginalTransactionValidation(OriginalTransactionVerdict.Unavailable, "Saved original is unavailable");
                stoppingToken.ThrowIfCancellationRequested();
                if (validation.Verdict == OriginalTransactionVerdict.Unavailable)
                {
                    LogPayjoinReceiverAmountRejected(_logger, session.InvoiceId, "original broadcast check: " + validation.Reason, null);
                    return;
                }
                var refreshed = await PayjoinReceiverSessionGuard.RefreshAfterExternalCheckAsync(
                    _sessionGuard, _sessionStore, stateContext, stoppingToken).ConfigureAwait(false);
                if (refreshed is null || refreshed.Session.IsCloseRequested) return;
                stateContext = refreshed;
                session = refreshed.Session;
                if (validation.Verdict != OriginalTransactionVerdict.Accepted)
                {
                    LogPayjoinReceiverAmountRejected(_logger, session.InvoiceId, "original broadcast check: " + validation.Reason, null);
                    if (guardedSession.State is not ReceiveSession.PayjoinProposal && validation.Verdict == OriginalTransactionVerdict.Rejected)
                    {
                        var current = _sessionStore.ReadPaymentSnapshot(persister);
                        if (current.Bridge?.ExpectedFinalTransactionId is null)
                            _sessionStore.FailPayment(persister, "original broadcast check: " + validation.Reason);
                    }
                    return;
                }
            }

            // Earlier states may still be waiting for a payload. Replayed proposals, however, must
            // pass the same gate before contributing inputs, signing or posting again.
            if (guardedSession.State is ReceiveSession.WantsInputs or ReceiveSession.WantsFeeRange or
                ReceiveSession.ProvisionalProposal &&
                ValidateAndAttachOriginalPayment(persister, receiverScript) is null)
            {
                return;
            }

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
                        var contributedCoinsForFeeRange = await TryGetRequiredPersistedContributedCoinsAsync(session, stoppingToken).ConfigureAwait(false);
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
                        var contributedCoinsForProposal = await TryGetRequiredPersistedContributedCoinsAsync(session, stoppingToken).ConfigureAwait(false);
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
                        var finalizationContext = new PayjoinReceiverProposalFinalizationContext(persister, session.StoreId, session.InvoiceId, PayjoinConstants.BitcoinCode);
                        await _proposalFinalizer.PostAsync(finalizationContext, payjoinProposal.Inner, stoppingToken).ConfigureAwait(false);
                        break;
                    }
                case ReceiveSession.ReceiverPendingFallback pendingFallback:
                    await _stateProcessor.ProcessPendingFallbackAsync(stateContext, pendingFallback.Inner, stoppingToken).ConfigureAwait(false);
                    break;
                case ReceiveSession.Monitor:
                    break;
                case ReceiveSession.Closed:
                    RemoveSession(session.InvoiceId, "receiver session closed", session.SessionInstanceId);
                    break;
            }
        }
        catch (PayjoinAccountingReconciliationDataException ex)
        {
            _sessionStore.FailPayment(persister, ex.Message);
        }
    }

    private async Task ContinueWithOutputsAsync(
        WantsOutputs proposal,
        PayjoinReceiverStateContext context,
        CancellationToken cancellationToken)
    {
        if (!proposal.ProposalTxidIsStable())
        {
            // A sender input that finalizes with a non-empty scriptSig (legacy, or nested SegWit
            // with its redeem-script push) changes the txid preimage when the sender signs the
            // payjoin proposal, so the final transaction id differs from anything derived on the
            // receiver side and settlement reconciliation could never match it. The session ends
            // here, before outputs are committed or inputs contributed; if the sender broadcasts
            // the original, the invoice is still credited through the platform's own address
            // tracking.
            LogPayjoinReceiverTxidUnstableSenderInput(_logger, context.InvoiceId, null);
            RemoveSession(context.InvoiceId, "sender inputs that finalize through the scriptSig are not supported", context.Session.SessionInstanceId, context.Persister.AttemptVersion);
            return;
        }

        // The original received in this tick is absent from the guard's initial ReplayResult.
        var payment = ValidateAndAttachOriginalPayment(context.Persister, context.ReceiverScript);
        if (payment is null)
        {
            return;
        }

        await ProcessWantsOutputsAsync(
            proposal,
            context.Persister,
            context.ReceiverScript,
            context.StoreId,
            context.InvoiceId,
            GetReservationExpiresAt(context.Session),
            payment.AmountSats,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessWantsOutputsAsync(
        WantsOutputs proposal,
        ReceiverAttemptOwner persister,
        byte[] receiverScript,
        string storeId,
        string invoiceId,
        DateTimeOffset reservationExpiresAt,
        long pinnedAmountSats,
        CancellationToken stoppingToken)
    {
        var settlementOutputs = await CreateSettlementOutputsOrCompleteUnsignedAttemptAsync(
            proposal.OutputSubstitution() == OutputSubstitution.Disabled,
            storeId, invoiceId, receiverScript, pinnedAmountSats, persister, stoppingToken).ConfigureAwait(false);
        if (settlementOutputs is null)
        {
            return;
        }

        try
        {
            using var wantsInputs = ApplySettlementOutputs(proposal, settlementOutputs, persister);
            LogPayjoinReceiverPreparedSettlementOutputs(_logger, invoiceId, null);
            await ProcessWantsInputsAsync(wantsInputs, persister, receiverScript, storeId, invoiceId, reservationExpiresAt, stoppingToken).ConfigureAwait(false);
        }
        catch (OutputSubstitutionException ex)
        {
            LogPayjoinReceiverSettlementOutputsFailed(_logger, invoiceId, ex.Message, ex);
            _sessionStore.TryCompleteUnsignedAttempt(invoiceId, persister.SessionInstanceId, persister.AttemptVersion, receiverScript);
        }
    }

    private async Task ProcessWantsInputsAsync(
        WantsInputs proposal,
        ReceiverAttemptOwner persister,
        byte[] receiverScript,
        string storeId,
        string invoiceId,
        DateTimeOffset reservationExpiresAt,
        CancellationToken stoppingToken)
    {
        var contributionResult = await TryCreateInputContributionAsync(proposal, receiverScript, storeId, invoiceId, reservationExpiresAt, persister, stoppingToken).ConfigureAwait(false);
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

    internal async Task<PayjoinReceiverOutputBuilder.OutputReplacement?> CreateSettlementOutputsOrCompleteUnsignedAttemptAsync(
        bool preserveReceiverScript,
        string storeId,
        string invoiceId,
        byte[] receiverScript,
        long pinnedAmountSats,
        ReceiverAttemptOwner persister, CancellationToken stoppingToken)
    {
        var settlementOutputs = await _outputBuilder.TryCreateSettlementOutputsAsync(
            storeId,
            invoiceId,
            receiverScript,
            preserveReceiverScript,
            pinnedAmountSats,
            stoppingToken).ConfigureAwait(false);
        if (settlementOutputs.Status == PayjoinReceiverOutputBuilder.OutputBuildStatus.Ready)
        {
            return settlementOutputs.Outputs;
        }

        LogPayjoinReceiverSettlementOutputsUnavailable(_logger, invoiceId, null);
        if (settlementOutputs.Status == PayjoinReceiverOutputBuilder.OutputBuildStatus.InvalidDestination)
        {
            _sessionStore.TryCompleteUnsignedAttempt(invoiceId, persister.SessionInstanceId, persister.AttemptVersion, receiverScript);
        }
        return null;
    }

    private WantsInputs ApplySettlementOutputs(WantsOutputs proposal, PayjoinReceiverOutputBuilder.OutputReplacement settlementOutputs, ReceiverAttemptOwner persister)
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
            _sessionStore.CommitReceiverOutputs(persister, capturingPersister.Events, settlementOutputs);
            return wantsInputs;
        }
        catch
        {
            // Nothing was persisted, so the next tick replays back into WantsOutputs and rebuilds.
            wantsInputs.Dispose();
            throw;
        }
    }

    private async Task<ReceiverInputContribution?> TryCreateInputContributionAsync(
        WantsInputs proposal,
        byte[] receiverScript,
        string storeId,
        string invoiceId,
        DateTimeOffset reservationExpiresAt,
        ReceiverAttemptOwner persister, CancellationToken stoppingToken)
    {
        var contribution = await _inputSelector.TryContributeInputsAsync(proposal, storeId, reservationExpiresAt, persister, stoppingToken).ConfigureAwait(false);
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
        _sessionStore.TryCompleteUnsignedAttempt(invoiceId, persister.SessionInstanceId, persister.AttemptVersion, receiverScript);
        return null;
    }

    private readonly record struct ReceiverInputContribution(WantsInputs ProposalWithInputs, ReceivedCoin[] ContributedCoins);

    private async Task FinalizeInputContributionAsync(
        ReceiverInputContribution contribution,
        ReceiverAttemptOwner persister,
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
        CancellationToken cancellationToken)
    {
        var contributedCoins = await _inputSelector.TryGetPersistedContributedCoinsAsync(session, cancellationToken).ConfigureAwait(false);
        if (contributedCoins is not null)
        {
            return contributedCoins;
        }

        LogPayjoinReceiverPersistedInputUnavailable(_logger, session.InvoiceId, null);
        // Keep the contributed outpoint and journal so a later poll can retry the same input.
        return null;
    }

    private static DateTimeOffset GetReservationExpiresAt(PayjoinReceiverSessionState session)
    {
        // TODO: Revisit whether receiver-input reservations should always live until MonitoringExpiresAt or use a shorter policy-derived lifetime.
        return session.MonitoringExpiresAt;
    }

    private PayjoinReceiverPaymentValidation.OriginalPayment? ValidateAndAttachOriginalPayment(ReceiverAttemptOwner owner, byte[] receiverScript)
    {
        // A new original can be persisted during this tick, after the guard's initial replay.
        // Read row, events and bridge together, still bound to this owner's expected version.
        var snapshot = _sessionStore.ReadPaymentSnapshot(owner);
        var session = snapshot.Session;
        var bridge = snapshot.Bridge;
        if (!PayjoinReceiverPaymentValidation.IsNegotiationAllowed(bridge)) return null;
        using var replay = PayjoinMethods.ReplayReceiverEventLog(PayjoinReceiverSessionStore.Snapshot(session));
        using var history = replay.SessionHistory();
        using var uri = history.PjUri();
        var originalBytes = history.FallbackTx();
        if (uri.AmountSats() is not > 0 or > long.MaxValue || originalBytes is not { Length: > 0 })
            return RejectPayment(snapshot.Session, owner, receiverScript, "missing positive session amount or original transaction");
        var network = _networkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode)?.NBitcoinNetwork
            ?? throw new InvalidOperationException("Fallback network unavailable.");
        var payment = PayjoinReceiverPaymentValidation.ValidateOriginalAmount(uri.AmountSats(), Transaction.Load(originalBytes, network), receiverScript);
        if (payment is null)
            return RejectPayment(session, owner, receiverScript, "original payment amount does not match the pinned invoice amount");
        using var state = replay.State();
        var outputs = PayjoinReceiverPaymentValidation.ReadCommittedOutputs(session.GetEvents());
        var proposalId = state is ReceiveSession.PayjoinProposal proposal
            ? PSBT.Parse(proposal.Inner.Psbt(), network).GetGlobalTransaction().GetHash().ToString() : null;
        var conflict = PayjoinReceiverPaymentValidation.FindPaymentConflict(payment, bridge, outputs,
            beforeOutputs: state is ReceiveSession.WantsOutputs, proposalId);
        if (conflict is not null) return RejectPayment(session, owner, receiverScript, conflict);
        if (bridge.FallbackTransactionId is null || !bridge.FallbackOutputIndex.HasValue ||
            !bridge.FallbackValueSats.HasValue || !bridge.EffectiveInvoiceValueSats.HasValue)
            _sessionStore.AttachOriginalPayment(owner, payment);
        return payment;
    }

    private PayjoinReceiverPaymentValidation.OriginalPayment? RejectPayment(PayjoinReceiverSessionState snapshot, ReceiverAttemptOwner owner, byte[] receiverScript, string reason)
    {
        LogPayjoinReceiverAmountRejected(_logger, snapshot.InvoiceId, reason, null);
        var rejection = _sessionStore.TryRejectEarlyOriginal(snapshot, receiverScript);
        if (rejection == PayjoinReceiverSessionStore.EarlyRejectionResult.RequiresRecovery)
            _sessionStore.FailPayment(owner, reason);
        // Keep the URI and complete event log when explicit manual recovery is required.
        return null;
    }

    private bool RemoveSession(string invoiceId, string reason, Guid? sessionInstanceId = null, int? expectedVersion = null)
    {
        if (!_sessionStore.RemoveSession(invoiceId, sessionInstanceId, expectedVersion))
        {
            return false;
        }

        LogPayjoinReceiverSessionRemoved(_logger, invoiceId, reason, null);
        return true;
    }

}
