using Payjoin;
using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using SystemUri = System.Uri;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal sealed class PayjoinReceiverStateProcessor : IPayjoinReceiverStateProcessor
{
    private readonly PayjoinReceiverSessionStore _sessionStore;
    private readonly IPayjoinReceiverRelayClient _relayClient;

    public PayjoinReceiverStateProcessor(
        PayjoinReceiverSessionStore sessionStore,
        IPayjoinReceiverRelayClient relayClient)
    {
        _sessionStore = sessionStore;
        _relayClient = relayClient;
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

        using var requestResponse = initialized.CreatePollRequest(context.OhttpRelayUrl.ToString());
        var responseBody = await _relayClient.SendAsync(
            new SystemUri(requestResponse.request.url, UriKind.Absolute),
            requestResponse.request.contentType,
            requestResponse.request.body,
            cancellationToken).ConfigureAwait(false);

        using var transition = initialized.ProcessResponse(responseBody, requestResponse.clientResponse);
        using var outcome = transition.Save(context.Persister);

        if (outcome is InitializedTransitionOutcome.Progress progress)
        {
            await ProcessUncheckedProposalAsync(context, progress.inner, continueWithOutputsAsync, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ProcessReplyableErrorAsync(
        PayjoinReceiverStateContext context,
        HasReplyableError replyableError,
        CancellationToken cancellationToken)
    {
        using var requestResponse = replyableError.CreateErrorRequest(context.OhttpRelayUrl.ToString());
        var responseBody = await _relayClient.SendAsync(
            new SystemUri(requestResponse.request.url, UriKind.Absolute),
            requestResponse.request.contentType,
            requestResponse.request.body,
            cancellationToken).ConfigureAwait(false);
        using var transition = replyableError.ProcessErrorResponse(responseBody, requestResponse.clientResponse);
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
        using var transition = proposal.CheckInputsNotOwned(new ReceiverScriptOwnedCallback(context.ReceiverScript));
        using var maybeInputsSeen = transition.Save(context.Persister);
        await ProcessMaybeInputsSeenAsync(context, maybeInputsSeen, continueWithOutputsAsync, cancellationToken).ConfigureAwait(false);
    }

    public async Task ProcessMaybeInputsSeenAsync(
        PayjoinReceiverStateContext context,
        MaybeInputsSeen proposal,
        Func<WantsOutputs, PayjoinReceiverStateContext, CancellationToken, Task> continueWithOutputsAsync,
        CancellationToken cancellationToken)
    {
        using var transition = proposal.CheckNoInputsSeenBefore(new NoInputsSeenCallback());
        using var outputsUnknown = transition.Save(context.Persister);
        await ProcessOutputsUnknownAsync(context, outputsUnknown, continueWithOutputsAsync, cancellationToken).ConfigureAwait(false);
    }

    public async Task ProcessOutputsUnknownAsync(
        PayjoinReceiverStateContext context,
        OutputsUnknown proposal,
        Func<WantsOutputs, PayjoinReceiverStateContext, CancellationToken, Task> continueWithOutputsAsync,
        CancellationToken cancellationToken)
    {
        using var transition = proposal.IdentifyReceiverOutputs(new ReceiverScriptOwnedCallback(context.ReceiverScript));
        using var wantsOutputs = transition.Save(context.Persister);
        await continueWithOutputsAsync(wantsOutputs, context, cancellationToken).ConfigureAwait(false);
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
        using var rejectionTransition = proposal.CheckBroadcastSuitability(null, new CloseRequestedBroadcastGuard(context.Session));

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

        await ProcessReplyableErrorAsync(context, hasReplyableError.inner, cancellationToken).ConfigureAwait(false);
        return true;
    }

    // TODO: Load all wallet-owned scripts from the store's derivation scheme and check the incoming script against that full set.
    internal sealed class ReceiverScriptOwnedCallback : IsScriptOwned
    {
        private readonly byte[] _script;

        public ReceiverScriptOwnedCallback(byte[] script)
        {
            _script = script;
        }

        public bool Callback(byte[] script) => script.AsSpan().SequenceEqual(_script);
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
}
