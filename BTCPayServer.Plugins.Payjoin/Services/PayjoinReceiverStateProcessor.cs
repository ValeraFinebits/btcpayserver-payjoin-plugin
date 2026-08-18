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
            requestResponse => (new SystemUri(requestResponse.Request.Url, UriKind.Absolute), requestResponse.Request.ContentType, requestResponse.Request.Body),
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
            requestResponse => (new SystemUri(requestResponse.Request.Url, UriKind.Absolute), requestResponse.Request.ContentType, requestResponse.Request.Body),
            cancellationToken).ConfigureAwait(false);
        using var relayRequestContext = requestResponse;
        using var transition = replyableError.ProcessErrorResponse(responseBody, requestResponse.ClientResponse);
        using var pendingFallback = transition.Save(context.Persister);
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

        // TODO: Reject or suspend proposals while cold-wallet ownership readiness is unverified.
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
        // fallback transaction once and share its authoritative input outpoints and output scripts
        // with the rest of the chain through the context.
        if (context.OriginalInputOutpoints.Count == 0)
        {
            var originalTransaction = ExtractTransactionFacts(proposal.ExtractTxToScheduleBroadcast());
            context.OriginalInputOutpoints = originalTransaction.InputOutpoints;
            context.OriginalOutputScripts = originalTransaction.OutputScripts;
        }

        var ownershipResolver = await GetOrCreateInputOwnershipResolverAsync(context, cancellationToken).ConfigureAwait(false);
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
            context.InvoiceId,
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
        var ownershipResolver = await GetOrCreateOutputOwnershipResolverAsync(context, cancellationToken).ConfigureAwait(false);
        using var transition = proposal.IdentifyReceiverOutputs(new WalletScriptOwnedCallback(ownershipResolver));
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

    private async Task<PayjoinScriptOwnershipResolver> GetOrCreateOutputOwnershipResolverAsync(
        PayjoinReceiverStateContext context,
        CancellationToken cancellationToken)
    {
        context.OutputOwnershipResolver ??= await _walletOwnershipService.CreateOutputResolverAsync(
            context.StoreId,
            context.ReceiverScript,
            context.OriginalOutputScripts,
            cancellationToken).ConfigureAwait(false);
        return context.OutputOwnershipResolver;
    }

    internal static PayjoinOriginalTransactionFacts ExtractTransactionFacts(byte[] transactionBytes)
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

        var scripts = new List<byte[]>(transaction.Outputs.Count);
        foreach (var output in transaction.Outputs)
        {
            scripts.Add(output.ScriptPubKey.ToBytes());
        }

        return new PayjoinOriginalTransactionFacts(inputOutpoints, scripts);
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

    // Output ownership remains script-based because the transaction output script is authoritative.
    internal sealed class WalletScriptOwnedCallback : IsScriptOwned
    {
        private readonly PayjoinScriptOwnershipResolver _resolver;

        public WalletScriptOwnedCallback(PayjoinScriptOwnershipResolver resolver)
        {
            _resolver = resolver;
        }

        public bool Callback(byte[] script) => _resolver.IsOwned(script);
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
