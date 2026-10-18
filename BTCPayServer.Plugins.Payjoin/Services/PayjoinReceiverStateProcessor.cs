using Payjoin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using SystemUri = System.Uri;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal sealed class PayjoinReceiverStateProcessor : IPayjoinReceiverStateProcessor
{
    private readonly PayjoinReceiverSessionStore _sessionStore;
    private readonly IPayjoinReceiverRelayRequestSender _relayRequestSender;
    private readonly IPayjoinWalletOwnershipService _walletOwnershipService;
    private readonly PayjoinSeenInputStore _seenInputStore;
    private readonly PayjoinMailroomManager _mailroomManager;

    public PayjoinReceiverStateProcessor(
        PayjoinReceiverSessionStore sessionStore,
        IPayjoinReceiverRelayRequestSender relayRequestSender,
        IPayjoinWalletOwnershipService walletOwnershipService,
        PayjoinSeenInputStore seenInputStore,
        PayjoinMailroomManager mailroomManager)
    {
        _sessionStore = sessionStore;
        _relayRequestSender = relayRequestSender;
        _walletOwnershipService = walletOwnershipService;
        _seenInputStore = seenInputStore;
        _mailroomManager = mailroomManager;
    }

    public async Task ProcessInitializedAsync(
        PayjoinReceiverStateContext context,
        Initialized initialized,
        Func<WantsOutputs, PayjoinReceiverStateContext, CancellationToken, Task> continueWithOutputsAsync,
        CancellationToken cancellationToken)
    {
        if (context.Session.IsCloseRequested)
        {
            _sessionStore.TryConsumeInitializedPollAfterCloseRequest(context.Session.InvoiceId, context.Session.SessionInstanceId);
        }

        var (responseBody, requestResponse) = await _relayRequestSender.SendAsync(
            context.StoreId,
            context.InvoiceId,
            initialized.CreatePollRequest,
            requestResponse => (new SystemUri(requestResponse.Request.Url, UriKind.Absolute), requestResponse.Request.ContentType, requestResponse.Request.Body),
            cancellationToken).ConfigureAwait(false);
        using var relayRequestContext = requestResponse;

        InitializedTransitionOutcome outcome;
        try
        {
            using var transition = initialized.ProcessResponse(responseBody, requestResponse.ClientResponse);
            outcome = transition.Save(context.Persister);
        }
        catch (ReceiverPersistedException.Transient ex)
        {
            _mailroomManager.MarkRelayFailedForSession(context.InvoiceId, new SystemUri(requestResponse.Request.Url, UriKind.Absolute));
            throw new HttpRequestException("Failed to process the OHTTP poll response.", ex);
        }

        using (outcome)
        {
            if (outcome is InitializedTransitionOutcome.Progress progress)
            {
                var currentContext = RefreshCloseRequestedContext(context);
                await ProcessUncheckedProposalAsync(currentContext, progress.Inner, continueWithOutputsAsync, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task ProcessReplyableErrorAsync(
        PayjoinReceiverStateContext context,
        HasReplyableError replyableError,
        CancellationToken cancellationToken)
    {
        var (responseBody, requestResponse) = await _relayRequestSender.SendAsync(
            context.StoreId,
            context.InvoiceId,
            replyableError.CreateErrorRequest,
            requestResponse => (new SystemUri(requestResponse.Request.Url, UriKind.Absolute), requestResponse.Request.ContentType, requestResponse.Request.Body),
            cancellationToken).ConfigureAwait(false);
        using var relayRequestContext = requestResponse;

        try
        {
            using var transition = replyableError.ProcessErrorResponse(responseBody, requestResponse.ClientResponse);
            using var pendingFallback = transition.Save(context.Persister);
        }
        catch (ReceiverPersistedException.Transient ex)
        {
            _mailroomManager.MarkRelayFailedForSession(context.InvoiceId, new SystemUri(requestResponse.Request.Url, UriKind.Absolute));
            throw new HttpRequestException("Failed to process the OHTTP error reply response.", ex);
        }
    }

    public Task ProcessPendingFallbackAsync(
        PayjoinReceiverStateContext context,
        ReceiverPendingFallback pendingFallback,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var transition = pendingFallback.Close();
        transition.Save(context.Persister);
        return Task.CompletedTask;
    }

    public async Task ProcessUncheckedProposalAsync(
        PayjoinReceiverStateContext context,
        UncheckedOriginalPayload proposal,
        Func<WantsOutputs, PayjoinReceiverStateContext, CancellationToken, Task> continueWithOutputsAsync,
        CancellationToken cancellationToken)
    {
        if (context.Session.IsCloseRequested)
        {
            if (await TryRejectCloseRequestedOriginalPayloadAsync(context, proposal, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            context.RemoveCloseRequestedSession(context.Session);
            return;
        }

        // TODO: Validate this untrusted PSBT with Bitcoin Core testmempoolaccept before contributing receiver inputs.
        using var transition = proposal.AssumeInteractiveReceiver();
        using var maybeInputsOwned = transition.Save(context.Persister);
        await ProcessMaybeInputsOwnedAsync(context, maybeInputsOwned, continueWithOutputsAsync, cancellationToken).ConfigureAwait(false);
    }

    public async Task ProcessMaybeInputsOwnedAsync(
        PayjoinReceiverStateContext context,
        MaybeInputsOwned proposal,
        Func<WantsOutputs, PayjoinReceiverStateContext, CancellationToken, Task> continueWithOutputsAsync,
        CancellationToken cancellationToken)
    {
        // A payload that arrived within this tick is not in the replayed history yet, so parse the
        // fallback transaction once and share its authoritative input outpoints
        // with the rest of the chain through the context.
        if (context.OriginalInputOutpoints.Count == 0)
        {
            context.OriginalInputOutpoints = ExtractOriginalInputOutpoints(proposal.ExtractTxToScheduleBroadcast());
        }

        var ownershipResolver = await GetOrCreateInputOwnershipResolverAsync(context, cancellationToken).ConfigureAwait(false);
        if (context.OriginalInputOutpoints.Any(input => ownershipResolver.GetOwnership(input) == PayjoinInputOwnership.Unknown))
        {
            // Retain MaybeInputsOwned and retry with fresh wallet evidence on the next tick.
            // Unknown must never reach the boolean native callback or its persisted transition.
            return;
        }

        using var transition = proposal.CheckInputsNotOwned(new WalletInputOwnedCallback(ownershipResolver));
        using var maybeInputsSeen = transition.Save(context.Persister);
        await ProcessMaybeInputsSeenAsync(context, maybeInputsSeen, continueWithOutputsAsync, cancellationToken).ConfigureAwait(false);
    }

    public async Task ProcessMaybeInputsSeenAsync(
        PayjoinReceiverStateContext context,
        MaybeInputsSeen proposal,
        Func<WantsOutputs, PayjoinReceiverStateContext, CancellationToken, Task> continueWithOutputsAsync,
        CancellationToken cancellationToken)
    {
        using var outputsUnknown = _seenInputStore.ExecuteSeenInputTransition(
            context.Persister,
            (callback, persister) =>
            {
                using var transition = proposal.CheckNoInputsSeenBefore(callback);
                return transition.Save(persister);
            });
        await ProcessOutputsUnknownAsync(context, outputsUnknown, continueWithOutputsAsync, cancellationToken).ConfigureAwait(false);
    }

    public async Task ProcessOutputsUnknownAsync(
        PayjoinReceiverStateContext context,
        OutputsUnknown proposal,
        Func<WantsOutputs, PayjoinReceiverStateContext, CancellationToken, Task> continueWithOutputsAsync,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var matcher = new PayjoinInvoiceOutputMatcher(context.ReceiverScript);
        using var transition = proposal.IdentifyReceiverOutputs(new InvoiceOutputCallback(matcher));
        using var wantsOutputs = transition.Save(context.Persister);
        await continueWithOutputsAsync(wantsOutputs, context, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PayjoinInputOwnershipResolver> GetOrCreateInputOwnershipResolverAsync(
        PayjoinReceiverStateContext context,
        CancellationToken cancellationToken)
    {
        context.InputOwnershipResolver ??= await _walletOwnershipService.CreateInputResolverAsync(
            context.StoreId,
            context.OriginalInputOutpoints,
            cancellationToken).ConfigureAwait(false);
        return context.InputOwnershipResolver;
    }

    internal static IReadOnlyList<NBitcoin.OutPoint> ExtractOriginalInputOutpoints(byte[] transactionBytes)
    {
        if (transactionBytes.Length == 0)
        {
            throw new InvalidOperationException("rust-payjoin returned an empty Original transaction.");
        }

        var transaction = NBitcoin.Transaction.Load(transactionBytes, NBitcoin.Network.Main);
        var inputOutpoints = new List<NBitcoin.OutPoint>(transaction.Inputs.Count);
        foreach (var input in transaction.Inputs)
        {
            inputOutpoints.Add(input.PrevOut);
        }

        return inputOutpoints;
    }

    private PayjoinReceiverStateContext RefreshCloseRequestedContext(PayjoinReceiverStateContext context)
    {
        if (context.Session.IsCloseRequested)
        {
            return context;
        }

        var latestSession = _sessionStore.GetAttempt(context.InvoiceId, context.Session.SessionInstanceId);
        if (context.Persister.AttemptVersion != latestSession.AttemptVersion)
            throw new PayjoinSessionStaleException();
        if (!latestSession.IsCloseRequested) return context;

        return new PayjoinReceiverStateContext(
            latestSession,
            context.Persister,
            context.ReceiverScript,
            context.StoreId,
            context.InvoiceId,
            context.RemoveCloseRequestedSession);
    }

    private async Task<bool> TryRejectCloseRequestedOriginalPayloadAsync(
        PayjoinReceiverStateContext context,
        UncheckedOriginalPayload proposal,
        CancellationToken cancellationToken)
    {
        // TODO: Replace this close-request workaround with a direct rust-payjoin/payjoin-ffi API for
        // creating a replyable receiver rejection from the current session state. The current bindings
        // do not expose persisted `error_state()` or an explicit `Unavailable`/session-closed reject path,
        // so we temporarily route invoice-closed sessions through `CheckBroadcastSuitability`.
        using var rejectionTransition = proposal.CheckBroadcastSuitability(minFeeRateSatPerKwu: null, canBroadcast: new CloseRequestedBroadcastGuard(context.Session));

        try
        {
            using var _ = rejectionTransition.Save(context.Persister);
            return false;
        }
        catch (ReceiverPersistedException ex)
        {
            if (await TryPostPersistedReplyableErrorAsync(context, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            ExceptionDispatchInfo.Capture(ex).Throw();
            throw;
        }
    }

    private async Task<bool> TryPostPersistedReplyableErrorAsync(
        PayjoinReceiverStateContext context,
        CancellationToken cancellationToken)
    {
        ReplayResult replay;
        try
        {
            replay = PayjoinMethods.ReplayReceiverEventLog(context.Persister);
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

        await ProcessReplyableErrorAsync(context, hasReplyableError.Inner, cancellationToken).ConfigureAwait(false);
        return true;
    }

    // Checks input ownership by authoritative wallet outpoint so a sender cannot disguise a
    // receiver-owned coin with forged PSBT metadata.
    internal sealed class WalletInputOwnedCallback : IsInputOwned
    {
        private readonly PayjoinInputOwnershipResolver _resolver;

        public WalletInputOwnedCallback(PayjoinInputOwnershipResolver resolver)
        {
            _resolver = resolver;
        }

        public bool Callback(OutPoint outpoint) => _resolver.IsOwned(outpoint.Txid, outpoint.Vout);
    }

    // Only the invoice output is selected for replacement.
    internal sealed class InvoiceOutputCallback : IsScriptOwned
    {
        private readonly PayjoinInvoiceOutputMatcher _matcher;

        public InvoiceOutputCallback(PayjoinInvoiceOutputMatcher matcher)
        {
            _matcher = matcher;
        }

        public bool Callback(byte[] script) => _matcher.Matches(script);
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
}
