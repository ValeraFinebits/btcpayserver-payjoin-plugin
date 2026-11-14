using Payjoin;
using BTCPayServer.Services.Wallets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin.RPC;
using NBXplorer.Models;
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
    internal static readonly TimeSpan FallbackBroadcastTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan FallbackRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly Action<ILogger, string, RPCErrorCode?, Exception?> LogFallbackRejected =
        LoggerMessage.Define<string, RPCErrorCode?>(LogLevel.Warning, new EventId(1, nameof(LogFallbackRejected)),
            "Original fallback rejected for {InvoiceId}; closing negotiation (RPC code {RPCCode}).");
    private static readonly Action<ILogger, string, string, Exception?> LogFallbackDeferred =
        LoggerMessage.Define<string, string>(LogLevel.Warning, new EventId(2, nameof(LogFallbackDeferred)),
            "Original fallback publication deferred for {InvoiceId}; retaining pending fallback ({Reason}).");
    private readonly PayjoinReceiverSessionStore _sessionStore;
    private readonly IPayjoinReceiverRelayRequestSender _relayRequestSender;
    private readonly IPayjoinWalletOwnershipService _walletOwnershipService;
    private readonly PayjoinSeenInputStore _seenInputStore;
    private readonly PayjoinMailroomManager _mailroomManager;
    private readonly Func<byte[], CancellationToken, Task<BroadcastResult?>> _broadcastFallbackAsync;
    private readonly ILogger<PayjoinReceiverStateProcessor> _logger;
    private readonly TimeSpan _fallbackBroadcastTimeout;
    private readonly TimeProvider _clock;
    private readonly object _fallbackSync = new();
    private readonly Dictionary<PayjoinOwnershipSessionKey, FallbackAttempt> _fallbackAttempts = new();

    public PayjoinReceiverStateProcessor(
        PayjoinReceiverSessionStore sessionStore,
        IPayjoinReceiverRelayRequestSender relayRequestSender,
        IPayjoinWalletOwnershipService walletOwnershipService,
        PayjoinSeenInputStore seenInputStore,
        PayjoinMailroomManager mailroomManager,
        ExplorerClientProvider explorerClientProvider,
        BTCPayNetworkProvider networkProvider,
        ILogger<PayjoinReceiverStateProcessor> logger)
        : this(sessionStore, relayRequestSender, walletOwnershipService, seenInputStore, mailroomManager,
            async (original, token) =>
            {
                var network = networkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode);
                var client = network is null ? null : explorerClientProvider.GetExplorerClient(network);
                return client is null ? null : await client.BroadcastAsync(
                    NBitcoin.Transaction.Load(original, network!.NBitcoinNetwork), token).ConfigureAwait(false);
            }, logger)
    {
    }

    internal PayjoinReceiverStateProcessor(
        PayjoinReceiverSessionStore sessionStore,
        IPayjoinReceiverRelayRequestSender relayRequestSender,
        IPayjoinWalletOwnershipService walletOwnershipService,
        PayjoinSeenInputStore seenInputStore,
        PayjoinMailroomManager mailroomManager,
        Func<byte[], CancellationToken, Task<BroadcastResult?>> broadcastFallbackAsync,
        ILogger<PayjoinReceiverStateProcessor>? logger = null,
        TimeSpan? fallbackBroadcastTimeout = null,
        TimeProvider? timeProvider = null)
    {
        _sessionStore = sessionStore;
        _relayRequestSender = relayRequestSender;
        _walletOwnershipService = walletOwnershipService;
        _seenInputStore = seenInputStore;
        _mailroomManager = mailroomManager;
        _broadcastFallbackAsync = broadcastFallbackAsync;
        _logger = logger ?? NullLogger<PayjoinReceiverStateProcessor>.Instance;
        _fallbackBroadcastTimeout = fallbackBroadcastTimeout ?? FallbackBroadcastTimeout;
        _clock = timeProvider ?? TimeProvider.System;
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
        _sessionStore.CompleteOwnershipLookup(context.Session);
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

    public async Task ProcessPendingFallbackAsync(
        PayjoinReceiverStateContext context,
        ReceiverPendingFallback pendingFallback,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = PayjoinOwnershipSessionKey.From(context.Session);
        if (!IsCurrentAttemptOpen(context))
        {
            ForgetIdleFallback(key);
            return;
        }
        var attempt = TryBeginFallback(key);
        if (attempt is null) return;
        var retry = false;
        try
        {
            if (!IsCurrentAttemptOpen(context)) return;
            _sessionStore.RequireNegotiationAllowed(context.Persister);
            RPCErrorCode? code;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(_fallbackBroadcastTimeout);
                var original = pendingFallback.FallbackTx();
                retry = true;
                var result = await _broadcastFallbackAsync(original, deadline.Token)
                    .WaitAsync(deadline.Token).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                code = result?.RPCCode;
                if (result is null || (result.Success ? code is not null : !ClosesFallback(code)))
                {
                    LogFallbackDeferred(_logger, context.InvoiceId, code?.ToString() ?? "unconfirmed response", null);
                    return;
                }
            }
            catch (RPCException ex) when (ClosesFallback(ex.RPCCode))
            {
                code = ex.RPCCode;
            }
            catch (Exception ex) when (ex is TimeoutException || PayjoinWalletContext.IsLookupUnavailable(ex, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                LogFallbackDeferred(_logger, context.InvoiceId, ex.GetType().Name, null);
                return;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentAttemptOpen(context))
            {
                retry = false;
                return;
            }
            _sessionStore.RequireNegotiationAllowed(context.Persister);
            if (code is not null && code != RPCErrorCode.RPC_TRANSACTION_ALREADY_IN_CHAIN)
                LogFallbackRejected(_logger, context.InvoiceId, code, null);
            using var transition = pendingFallback.Close();
            transition.Save(context.Persister);
            retry = false;
        }
        finally
        {
            lock (_fallbackSync)
            {
                if (retry)
                {
                    attempt.Running = false;
                    attempt.RetryAfter = _clock.GetUtcNow() + FallbackRetryDelay;
                }
                else _fallbackAttempts.Remove(key);
            }
        }
    }

    private bool IsCurrentAttemptOpen(PayjoinReceiverStateContext context)
    {
        var current = _sessionStore.GetAttempt(context.InvoiceId, context.Persister.SessionInstanceId);
        if (current.AttemptVersion != context.Persister.AttemptVersion) throw new PayjoinSessionStaleException();
        return !current.IsCloseRequested && current.MonitoringExpiresAt > _clock.GetUtcNow();
    }

    private FallbackAttempt? TryBeginFallback(PayjoinOwnershipSessionKey key)
    {
        lock (_fallbackSync)
        {
            var now = _clock.GetUtcNow();
            foreach (var expired in _fallbackAttempts.Where(pair => !pair.Value.Running && pair.Value.RetryAfter <= now).Select(pair => pair.Key).ToArray())
                _fallbackAttempts.Remove(expired);
            if (_fallbackAttempts.ContainsKey(key)) return null;
            var attempt = new FallbackAttempt();
            _fallbackAttempts.Add(key, attempt);
            return attempt;
        }
    }

    private void ForgetIdleFallback(PayjoinOwnershipSessionKey key)
    {
        lock (_fallbackSync)
            if (_fallbackAttempts.TryGetValue(key, out var attempt) && !attempt.Running) _fallbackAttempts.Remove(key);
    }

    private sealed class FallbackAttempt
    {
        internal bool Running { get; set; } = true;
        internal DateTimeOffset RetryAfter { get; set; }
    }

    private static bool ClosesFallback(RPCErrorCode? code) => code is
        RPCErrorCode.RPC_TRANSACTION_ALREADY_IN_CHAIN or RPCErrorCode.RPC_DESERIALIZATION_ERROR;

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
        // A payload received in this tick is not in the replayed history yet.
        context.OriginalTransaction ??= ExtractTransactionFacts(proposal.ExtractTxToScheduleBroadcast());

        var ownership = await GetOrCreateInputOwnershipLookupAsync(context, cancellationToken).ConfigureAwait(false);
        if (!IsCurrentAttemptOpen(context)) return;
        if (ownership.Status == PayjoinOwnershipLookupStatus.Exhausted)
        {
            _sessionStore.RequireNegotiationAllowed(context.Persister);
            using var cancellation = proposal.Cancel();
            using var pendingFallback = cancellation.Save(context.Persister);
            _sessionStore.CompleteOwnershipLookup(context.Session);
            if (pendingFallback is not null)
                await ProcessPendingFallbackAsync(context, pendingFallback, cancellationToken).ConfigureAwait(false);
            return;
        }
        var ownershipResolver = ownership.Resolver;
        if (ownershipResolver is null || context.OriginalInputOutpoints.Any(input => ownershipResolver.GetOwnership(input) == PayjoinInputOwnership.Unknown))
        {
            // Retain MaybeInputsOwned; the lookup policy controls retries and cooldown.
            // Unknown must never reach the boolean native callback or its persisted transition.
            return;
        }

        using var transition = proposal.CheckInputsNotOwned(new WalletInputOwnedCallback(ownershipResolver));
        using var maybeInputsSeen = transition.Save(context.Persister);
        _sessionStore.CompleteOwnershipLookup(context.Session);
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

    private async Task<PayjoinOwnershipLookupResult> GetOrCreateInputOwnershipLookupAsync(
        PayjoinReceiverStateContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.InputOwnershipResolver is not null) return PayjoinOwnershipLookupResult.Ready(context.InputOwnershipResolver);
        var result = await _walletOwnershipService.CreateResolverAsync(
            context.Session,
            context.OriginalTransaction ?? throw new InvalidOperationException("The Original transaction is unavailable for ownership checks."),
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Status == PayjoinOwnershipLookupStatus.Ready) context.InputOwnershipResolver = result.Resolver;
        return result;
    }

    internal static IReadOnlyList<NBitcoin.OutPoint> ExtractOriginalInputOutpoints(byte[] transactionBytes)
        => ExtractTransactionFacts(transactionBytes).InputOutpoints;

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

        return new PayjoinOriginalTransactionFacts(transaction.GetHash(), inputOutpoints);
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
