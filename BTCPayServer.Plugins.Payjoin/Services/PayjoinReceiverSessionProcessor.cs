using BTCPayServer.Services.Wallets;
using Microsoft.Extensions.Logging;
using Payjoin;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SystemUri = System.Uri;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal sealed class PayjoinReceiverSessionProcessor : IPayjoinReceiverSessionProcessor
{
    private static readonly Action<ILogger, string, Exception?> LogPayjoinReceiverPollingFailedForInvoice =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(2, nameof(LogPayjoinReceiverPollingFailedForInvoice)),
            "Payjoin receiver polling failed for {InvoiceId}");
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
    private readonly IPayjoinReceiverSessionGuard _sessionGuard;
    private readonly IPayjoinReceiverStateProcessor _stateProcessor;
    private readonly IPayjoinReceiverOutputBuilder _outputBuilder;
    private readonly IPayjoinReceiverInputSelector _inputSelector;
    private readonly IPayjoinReceiverProposalFinalizer _proposalFinalizer;
    private readonly ILogger<PayjoinReceiverSessionProcessor> _logger;
    private readonly IPayjoinStoreSettingsRepository _storeSettingsRepository;

    public PayjoinReceiverSessionProcessor(
        PayjoinReceiverSessionStore sessionStore,
        IPayjoinReceiverSessionGuard sessionGuard,
        IPayjoinReceiverStateProcessor stateProcessor,
        IPayjoinReceiverOutputBuilder outputBuilder,
        IPayjoinReceiverInputSelector inputSelector,
        IPayjoinReceiverProposalFinalizer proposalFinalizer,
        IPayjoinStoreSettingsRepository storeSettingsRepository,
        ILogger<PayjoinReceiverSessionProcessor> logger)
    {
        _sessionStore = sessionStore;
        _sessionGuard = sessionGuard;
        _stateProcessor = stateProcessor;
        _outputBuilder = outputBuilder;
        _inputSelector = inputSelector;
        _proposalFinalizer = proposalFinalizer;
        _storeSettingsRepository = storeSettingsRepository;
        _logger = logger;
    }

    // TODO: Process sessions concurrently instead of sequentially.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Session processing isolates per-session failures so one receiver session does not stop the polling loop.")]
    public async Task ProcessTickAsync(CancellationToken stoppingToken)
    {
        foreach (var session in _sessionStore.GetSessions())
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

        switch (guardedSession.State)
        {
            case ReceiveSession.Initialized initialized:
                await _stateProcessor.ProcessInitializedAsync(stateContext, initialized.inner, ContinueWithOutputsAsync, stoppingToken).ConfigureAwait(false);
                break;
            case ReceiveSession.HasReplyableError hasReplyableError:
                await _stateProcessor.ProcessReplyableErrorAsync(stateContext, hasReplyableError.inner, stoppingToken).ConfigureAwait(false);
                break;
            case ReceiveSession.UncheckedOriginalPayload payload:
                await _stateProcessor.ProcessUncheckedProposalAsync(stateContext, payload.inner, ContinueWithOutputsAsync, stoppingToken).ConfigureAwait(false);
                break;
            case ReceiveSession.MaybeInputsOwned maybeInputsOwned:
                await _stateProcessor.ProcessMaybeInputsOwnedAsync(stateContext, maybeInputsOwned.inner, ContinueWithOutputsAsync, stoppingToken).ConfigureAwait(false);
                break;
            case ReceiveSession.MaybeInputsSeen maybeInputsSeen:
                await _stateProcessor.ProcessMaybeInputsSeenAsync(stateContext, maybeInputsSeen.inner, ContinueWithOutputsAsync, stoppingToken).ConfigureAwait(false);
                break;
            case ReceiveSession.OutputsUnknown outputsUnknown:
                await _stateProcessor.ProcessOutputsUnknownAsync(stateContext, outputsUnknown.inner, ContinueWithOutputsAsync, stoppingToken).ConfigureAwait(false);
                break;
            case ReceiveSession.WantsOutputs wantsOutputs:
                await ContinueWithOutputsAsync(wantsOutputs.inner, stateContext, stoppingToken).ConfigureAwait(false);
                break;
            case ReceiveSession.WantsInputs wantsInputs:
                await ProcessWantsInputsAsync(wantsInputs.inner, persister, receiverScript, session.OhttpRelayUrl!, session.StoreId, session.InvoiceId, stoppingToken).ConfigureAwait(false);
                break;
            case ReceiveSession.WantsFeeRange wantsFeeRange:
                {
                    var contributedCoinsForFeeRange = await TryGetRequiredPersistedContributedCoinsAsync(session, "persisted receiver input unavailable", stoppingToken).ConfigureAwait(false);
                    if (contributedCoinsForFeeRange is null)
                    {
                        return;
                    }

                    await _proposalFinalizer.FinalizeAsync(
                        new PayjoinReceiverProposalFinalizationContext(persister, session.OhttpRelayUrl!, session.StoreId),
                        wantsFeeRange.inner,
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
                        new PayjoinReceiverProposalFinalizationContext(persister, session.OhttpRelayUrl!, session.StoreId),
                        provisionalProposal.inner,
                        contributedCoinsForProposal,
                        stoppingToken).ConfigureAwait(false);
                    break;
                }
            case ReceiveSession.PayjoinProposal payjoinProposal:
                await _proposalFinalizer.PostAsync(
                    new PayjoinReceiverProposalFinalizationContext(persister, session.OhttpRelayUrl!, session.StoreId),
                    payjoinProposal.inner,
                    stoppingToken).ConfigureAwait(false);
                break;
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
            context.OhttpRelayUrl,
            context.StoreId,
            context.InvoiceId,
            cancellationToken);
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
        var exactPaymentOutputs = await _outputBuilder.TryCreateExactPaymentOutputsAsync(storeId, invoiceId, receiverScript, stoppingToken).ConfigureAwait(false);
        if (exactPaymentOutputs is null)
        {
            LogPayjoinReceiverExactPaymentOutputsUnavailable(_logger, invoiceId, null);
            RemoveSession(invoiceId, "exact-payment outputs unavailable");
            return;
        }

        try
        {
            using var modified = proposal.ReplaceReceiverOutputs(exactPaymentOutputs.ExactPaymentOutputs, exactPaymentOutputs.ReceiverChangeScript);
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

    private async Task ProcessWantsInputsAsync(
        WantsInputs proposal,
        JsonReceiverSessionPersister persister,
        byte[] receiverScript,
        SystemUri ohttpRelayUrl,
        string storeId,
        string invoiceId,
        CancellationToken stoppingToken)
    {
        // TODO: Restore `proposal.TryPreservingPrivacy(receiverInputs)` only after rust-payjoin/payjoin-ffi lets us identify which `ReceivedCoin` was actually selected.
        // TODO: We must persist only the truly contributed coin(s) into `contributedCoins`; otherwise the signing step can treat unrelated wallet inputs as receiver-owned and produce an invalid proposal.
        var contribution = await _inputSelector.TryContributeInputsAsync(proposal, storeId, stoppingToken).ConfigureAwait(false);
        if (contribution.ProposalWithInputs is null || contribution.ContributedCoins is null)
        {
            LogPayjoinReceiverInputContributionFailed(_logger, invoiceId, contribution.FailureMessage, null);
            RemoveSession(invoiceId, "no receiver input available for payjoin contribution");
            return;
        }

        try
        {
            if (!_sessionStore.TryPersistContributedInput(invoiceId, contribution.ContributedCoins[0].OutPoint))
            {
                RemoveSession(invoiceId, "failed to persist contributed receiver input");
                return;
            }
            using var transition = contribution.ProposalWithInputs.CommitInputs();
            using var wantsFeeRange = transition.Save(persister);
            await _proposalFinalizer.FinalizeAsync(
                new PayjoinReceiverProposalFinalizationContext(persister, ohttpRelayUrl, storeId),
                wantsFeeRange,
                contribution.ContributedCoins,
                stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            contribution.ProposalWithInputs.Dispose();
        }
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
