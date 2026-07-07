using Payjoin;
using System;
using System.Collections.Generic;
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

    public PayjoinReceiverStateProcessor(
        PayjoinReceiverSessionStore sessionStore,
        IPayjoinReceiverRelayRequestSender relayRequestSender,
        IPayjoinWalletOwnershipService walletOwnershipService,
        PayjoinSeenInputStore seenInputStore)
    {
        _sessionStore = sessionStore;
        _relayRequestSender = relayRequestSender;
        _walletOwnershipService = walletOwnershipService;
        _seenInputStore = seenInputStore;
    }

    public async Task ProcessInitializedAsync(
        PayjoinReceiverStateContext context,
        Initialized initialized,
        Func<WantsOutputs, PayjoinReceiverStateContext, CancellationToken, Task> continueWithOutputsAsync,
        CancellationToken cancellationToken)
    {
        if (context.Session.IsCloseRequested)
        {
            _sessionStore.TryConsumeInitializedPollAfterCloseRequest(context.Session.InvoiceId);
        }

        var (responseBody, requestResponse) = await _relayRequestSender.SendAsync(
            context.StoreId,
            context.InvoiceId,
            initialized.CreatePollRequest,
            requestResponse => (new SystemUri(requestResponse.Request.Url), requestResponse.Request.ContentType, requestResponse.Request.Body),
            cancellationToken).ConfigureAwait(false);
        using var relayRequestContext = requestResponse;

        using var transition = initialized.ProcessResponse(responseBody, requestResponse.ClientResponse);
        using var outcome = transition.Save(context.Persister);

        if (outcome is InitializedTransitionOutcome.Progress progress)
        {
            var currentContext = RefreshCloseRequestedContext(context);
            await ProcessUncheckedProposalAsync(currentContext, progress.Inner, continueWithOutputsAsync, cancellationToken).ConfigureAwait(false);
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
            requestResponse => (new SystemUri(requestResponse.Request.Url), requestResponse.Request.ContentType, requestResponse.Request.Body),
            cancellationToken).ConfigureAwait(false);
        using var relayRequestContext = requestResponse;
        using var transition = replyableError.ProcessErrorResponse(responseBody, requestResponse.ClientResponse);
        transition.Save(context.Persister);
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
        // A payload that arrived within this tick is not in the replayed history yet, so the original
        // transaction's output scripts are taken from the proposal itself and shared with the rest of
        // the chain through the context.
        if (context.OriginalOutputScripts.Count == 0)
        {
            context.OriginalOutputScripts = ExtractOutputScripts(proposal.ExtractTxToScheduleBroadcast());
        }

        var ownershipResolver = await GetOrCreateOwnershipResolverAsync(context, cancellationToken).ConfigureAwait(false);
        using var transition = proposal.CheckInputsNotOwned(new WalletScriptOwnedCallback(ownershipResolver));
        using var maybeInputsSeen = transition.Save(context.Persister);
        await ProcessMaybeInputsSeenAsync(context, maybeInputsSeen, continueWithOutputsAsync, cancellationToken).ConfigureAwait(false);
    }

    public async Task ProcessMaybeInputsSeenAsync(
        PayjoinReceiverStateContext context,
        MaybeInputsSeen proposal,
        Func<WantsOutputs, PayjoinReceiverStateContext, CancellationToken, Task> continueWithOutputsAsync,
        CancellationToken cancellationToken)
    {
        using var transition = proposal.CheckNoInputsSeenBefore(new PersistentInputsSeenCallback(_seenInputStore));
        using var outputsUnknown = transition.Save(context.Persister);
        await ProcessOutputsUnknownAsync(context, outputsUnknown, continueWithOutputsAsync, cancellationToken).ConfigureAwait(false);
    }

    public async Task ProcessOutputsUnknownAsync(
        PayjoinReceiverStateContext context,
        OutputsUnknown proposal,
        Func<WantsOutputs, PayjoinReceiverStateContext, CancellationToken, Task> continueWithOutputsAsync,
        CancellationToken cancellationToken)
    {
        var ownershipResolver = await GetOrCreateOwnershipResolverAsync(context, cancellationToken).ConfigureAwait(false);
        using var transition = proposal.IdentifyReceiverOutputs(new WalletScriptOwnedCallback(ownershipResolver));
        using var wantsOutputs = transition.Save(context.Persister);
        await continueWithOutputsAsync(wantsOutputs, context, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PayjoinScriptOwnershipResolver> GetOrCreateOwnershipResolverAsync(
        PayjoinReceiverStateContext context,
        CancellationToken cancellationToken)
    {
        context.OwnershipResolver ??= await _walletOwnershipService.CreateResolverAsync(
            context.StoreId,
            context.ReceiverScript,
            context.OriginalOutputScripts,
            cancellationToken).ConfigureAwait(false);
        return context.OwnershipResolver;
    }

    internal static IReadOnlyList<byte[]> ExtractOutputScripts(byte[] transactionBytes)
    {
        if (transactionBytes.Length == 0)
        {
            return Array.Empty<byte[]>();
        }

        var transaction = NBitcoin.Transaction.Load(transactionBytes, NBitcoin.Network.Main);
        var scripts = new List<byte[]>(transaction.Outputs.Count);
        foreach (var output in transaction.Outputs)
        {
            scripts.Add(output.ScriptPubKey.ToBytes());
        }

        return scripts;
    }

    private PayjoinReceiverStateContext RefreshCloseRequestedContext(PayjoinReceiverStateContext context)
    {
        if (context.Session.IsCloseRequested)
        {
            return context;
        }

        if (!_sessionStore.TryGetSession(context.InvoiceId, out var latestSession) || latestSession is null || !latestSession.IsCloseRequested)
        {
            return context;
        }

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

    // Checks an input/output script against the store's full derivation scheme (not just the single
    // invoice address) so a sender cannot slip in a receiver-owned input or have a receiver output
    // misclassified as the sender's.
    internal sealed class WalletScriptOwnedCallback : IsScriptOwned
    {
        private readonly PayjoinScriptOwnershipResolver _resolver;

        public WalletScriptOwnedCallback(PayjoinScriptOwnershipResolver resolver)
        {
            _resolver = resolver;
        }

        public bool Callback(byte[] script) => _resolver.IsOwned(script);
    }

    // Records every inspected outpoint and reports whether it had been seen before, rejecting probing
    // attempts and re-entrant payjoins that replay a prior proposal's inputs.
    internal sealed class PersistentInputsSeenCallback : IsOutputKnown
    {
        private readonly PayjoinSeenInputStore _seenInputStore;

        public PersistentInputsSeenCallback(PayjoinSeenInputStore seenInputStore)
        {
            _seenInputStore = seenInputStore;
        }

        public bool Callback(OutPoint outpoint) =>
            _seenInputStore.MarkSeenAndWasPresent(outpoint.Txid, checked((long)outpoint.Vout));
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
