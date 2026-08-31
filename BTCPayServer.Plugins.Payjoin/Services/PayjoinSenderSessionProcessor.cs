using BTCPayServer.Abstractions;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer;
using Payjoin;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SystemUri = System.Uri;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal interface IPayjoinSenderSessionProcessor
{
    Task ProcessTickAsync(CancellationToken stoppingToken);

    Task<PayjoinSenderCancelResult> CancelAsync(string storeId, string senderSessionId, CancellationToken cancellationToken);

    /// <summary>True once the signed original has been posted to the directory.</summary>
    bool HasBeenShared(PayjoinSenderSessionState session);
}

/// <summary>The outcome of an operator's request to stop a session.</summary>
internal sealed record PayjoinSenderCancelResult(bool Success, string? BroadcastTransactionId, string? Error)
{
    /// <summary>The payjoin was abandoned and the plain payment went out instead.</summary>
    public static PayjoinSenderCancelResult Broadcast(string transactionId) => new(true, transactionId, null);

    /// <summary>Nothing had reached the network, so the session simply ended.</summary>
    public static PayjoinSenderCancelResult Dropped() => new(true, null, null);

    public static PayjoinSenderCancelResult Failed(string error) => new(false, null, error);
}

/// <summary>
/// Drives every pending sender session one step per tick through the rust-payjoin sender state
/// machine: post the original PSBT, poll for the receiver's proposal, sign and broadcast the
/// proposal when it arrives, and broadcast the original transaction when the library moves the
/// session to its fallback state. Every transition persists to the session's event log first, so
/// a restart replays the log and resumes exactly where the previous run stopped.
/// </summary>
internal sealed class PayjoinSenderSessionProcessor : IPayjoinSenderSessionProcessor
{
    private static readonly Action<ILogger, string, Exception?> LogSenderSessionFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, nameof(LogSenderSessionFailed)),
            "Payjoin sender session {SenderSessionId} failed");
    private static readonly Action<ILogger, string, Exception?> LogSenderSessionTransient =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(2, nameof(LogSenderSessionTransient)),
            "Payjoin sender session {SenderSessionId} hit a transient error; it retries next tick");
    private static readonly Action<ILogger, string, string, Exception?> LogSenderSessionBroadcast =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(3, nameof(LogSenderSessionBroadcast)),
            "Payjoin sender session {SenderSessionId} broadcast {TransactionId}");
    private static readonly Action<ILogger, string, string, Exception?> LogProposalAwaitingSignature =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(5, nameof(LogProposalAwaitingSignature)),
            "Payjoin sender session {SenderSessionId} needs a second signature on pending transaction {PendingTransactionId}");
    private readonly PayjoinSenderSessionStore _senderSessionStore;
    private readonly IPayjoinReceiverRelayRequestSender _relayRequestSender;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly StoreRepository _storeRepository;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly ExplorerClientProvider _explorerClientProvider;
    private readonly PendingTransactionService _pendingTransactionService;
    private readonly PayjoinSenderSignatureHandler _signatureHandler;
    private readonly ILogger<PayjoinSenderSessionProcessor> _logger;

    internal PayjoinSenderSessionProcessor(
        PayjoinSenderSessionStore senderSessionStore,
        IPayjoinReceiverRelayRequestSender relayRequestSender,
        BTCPayNetworkProvider networkProvider,
        StoreRepository storeRepository,
        PaymentMethodHandlerDictionary handlers,
        ExplorerClientProvider explorerClientProvider,
        PendingTransactionService pendingTransactionService,
        PayjoinSenderSignatureHandler signatureHandler,
        ILogger<PayjoinSenderSessionProcessor> logger)
    {
        _senderSessionStore = senderSessionStore;
        _relayRequestSender = relayRequestSender;
        _networkProvider = networkProvider;
        _storeRepository = storeRepository;
        _handlers = handlers;
        _explorerClientProvider = explorerClientProvider;
        _pendingTransactionService = pendingTransactionService;
        _signatureHandler = signatureHandler;
        _logger = logger;
    }

    public async Task ProcessTickAsync(CancellationToken stoppingToken)
    {
        // Sessions waiting for an off-server signature come first. Their signature arrives as an
        // in-memory event that a restart can lose, and a cancelled or expired transaction sends
        // no event at all, so this sweep is what makes the off-server path reliable.
        await _signatureHandler.ReconcileAsync(stoppingToken).ConfigureAwait(false);

        // Each session runs its own guarded task: sessions advance independently, and one
        // session's long relay poll must not delay every session behind it.
        await Task.WhenAll(_senderSessionStore.GetPendingSessions()
            .Select(session => ProcessSessionGuardedAsync(session, stoppingToken))).ConfigureAwait(false);
    }

    private async Task ProcessSessionGuardedAsync(PayjoinSenderSessionState session, CancellationToken stoppingToken)
    {
        try
        {
            await ProcessSessionAsync(session, stoppingToken).ConfigureAwait(false);
        }
        catch (PayjoinReceiverRelayTimeoutException ex)
        {
            // A stalled relay is this session's problem, not the tick's. It derives from
            // TaskCanceledException, so without this it would leave as a cancellation.
            LogSenderSessionTransient(_logger, session.SenderSessionId, ex);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SenderPersistedException.Transient ex)
        {
            LogSenderSessionTransient(_logger, session.SenderSessionId, ex);
        }
        catch (SenderReplayException ex)
        {
            await FailSessionAsync(session, $"sender session replay failed: {ex.Message}", ex, stoppingToken).ConfigureAwait(false);
        }
        catch (UniffiException ex)
        {
            await FailSessionAsync(session, $"sender session failed: {ex.Message}", ex, stoppingToken).ConfigureAwait(false);
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            LogSenderSessionTransient(_logger, session.SenderSessionId, ex);
        }
        catch (PayjoinSenderBroadcastException ex)
        {
            // The transaction is signed and valid as far as this plugin can tell, so a node
            // that refuses it now gets another chance on the next tick.
            LogSenderSessionTransient(_logger, session.SenderSessionId, ex);
        }
        catch (InvalidOperationException ex)
        {
            await FailSessionAsync(session, ex.Message, ex, stoppingToken).ConfigureAwait(false);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex)
        {
            // A concurrent writer won a store transition; whatever it did stands and this
            // session is picked up again next tick.
            LogSenderSessionTransient(_logger, session.SenderSessionId, ex);
        }
    }

    /// <summary>
    /// Stops a session at the operator's request. This is the control payjoin-cli offers as
    /// `cancel`, and it means "stop the payjoin", not "stop the payment": whenever the original
    /// transaction is signed, it goes to the network so the payment still completes. Only a
    /// session whose original was never signed ends with nothing broadcast.
    /// </summary>
    public async Task<PayjoinSenderCancelResult> CancelAsync(
        string storeId,
        string senderSessionId,
        CancellationToken cancellationToken)
    {
        if (!_senderSessionStore.TryGetSession(senderSessionId, out var session) ||
            session is null ||
            !string.Equals(session.StoreId, storeId, StringComparison.Ordinal))
        {
            return PayjoinSenderCancelResult.Failed("The payjoin session was not found.");
        }

        if (session.Status is not (PayjoinSenderSessionStatus.Pending or PayjoinSenderSessionStatus.AwaitingSignature))
        {
            return PayjoinSenderCancelResult.Failed("The payjoin session already ended.");
        }

        var network = _networkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode)
            ?? throw new InvalidOperationException("BTC network not available");

        // Withdraw any open signing request first, whichever round it belongs to. The operator
        // has decided, so nothing further should be asked of them.
        await CancelPendingTransactionAsync(session).ConfigureAwait(false);

        // Whether the payment can still be dropped turns on one fact: has the signed original
        // left this server? Before it is posted to the directory nobody else holds it, so cancel
        // means cancel — nothing is broadcast and the coins are free again. That covers a
        // session still waiting for its first signature and a hot session the poller has not
        // posted yet. Once the original has been posted, the receiver holds a fully signed
        // transaction it can broadcast at any time; "dropped" would then misdescribe coins that
        // are not free, so the only honest action left is to make the payment now, without the
        // payjoin.
        if (session.OriginalTransactionHex is null || !HasBeenShared(session))
        {
            PayjoinSenderSessionCloser.TryClose(_senderSessionStore.CreatePersister(session.SenderSessionId));
            _senderSessionStore.CompleteSession(
                session.SenderSessionId,
                PayjoinSenderSessionStatus.Failed,
                broadcastTransactionId: null,
                "the operator cancelled the payment before it was shared with the receiver");
            await PayjoinSenderSessionResourceReleaser
                .ReleaseAsync(_pendingTransactionService, _senderSessionStore, session).ConfigureAwait(false);
            return PayjoinSenderCancelResult.Dropped();
        }

        // The original is signed, so the payment goes out as a plain transaction. This does not
        // ask the library for its fallback copy: it closes the sender session as soon as it hands
        // over a proposal, and the payment must stay available after that.
        var fallbackTransaction = Transaction.Parse(session.OriginalTransactionHex, network.NBitcoinNetwork);
        try
        {
            await BroadcastAsync(network, fallbackTransaction, cancellationToken).ConfigureAwait(false);
        }
        catch (PayjoinSenderBroadcastException ex)
        {
            // The node refused the plain payment, most likely because the payjoin itself just
            // reached the network first. Nothing is lost: the session stays live and the sweep
            // settles it on the next tick with whatever actually happened.
            return PayjoinSenderCancelResult.Failed(
                $"The plain payment could not be broadcast: {ex.Message}");
        }
        if (!PayjoinSenderSessionCloser.TryClose(_senderSessionStore.CreatePersister(session.SenderSessionId)))
        {
            LogSenderSessionTransient(_logger, session.SenderSessionId, null!);
        }

        var fallbackTxId = fallbackTransaction.GetHash().ToString();
        LogSenderSessionBroadcast(_logger, session.SenderSessionId, fallbackTxId, null);
        _senderSessionStore.CompleteSession(
            session.SenderSessionId,
            PayjoinSenderSessionStatus.CompletedFallback,
            fallbackTxId,
            failureMessage: null);
        await PayjoinSenderSessionResourceReleaser
            .ReleaseAsync(_pendingTransactionService, _senderSessionStore, session).ConfigureAwait(false);
        return PayjoinSenderCancelResult.Broadcast(fallbackTxId);
    }

    /// <summary>
    /// True once the signed original has been posted to the directory. The library's replay
    /// answers this exactly: a session still in WithReplyKey has built the request but never
    /// sent it; every later state means the receiver may hold the original.
    /// </summary>
    public bool HasBeenShared(PayjoinSenderSessionState session)
    {
        if (session.Events.Length == 0)
        {
            return false;
        }

        try
        {
            using var replay = PayjoinMethods.ReplaySenderEventLog(_senderSessionStore.CreatePersister(session.SenderSessionId));
            using var state = replay.State();
            return state is not SendSession.WithReplyKey;
        }
        catch (Exception ex) when (ex is SenderReplayException or SenderPersistedException or UniffiException)
        {
            // A log that cannot be replayed cannot prove the original stayed home; assume the
            // receiver may have it and keep the payment.
            return true;
        }
    }

    private async Task CancelPendingTransactionAsync(PayjoinSenderSessionState session)
    {
        if (session.PendingTransactionId is null)
        {
            return;
        }

        await _pendingTransactionService.CancelPendingTransaction(
            new PendingTransactionService.PendingTransactionFullId(
                PayjoinConstants.BitcoinCode,
                session.StoreId,
                session.PendingTransactionId)).ConfigureAwait(false);
    }

    private async Task ProcessSessionAsync(PayjoinSenderSessionState session, CancellationToken cancellationToken)
    {
        var network = _networkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode)
            ?? throw new InvalidOperationException("BTC network not available");

        var persister = _senderSessionStore.CreatePersister(session.SenderSessionId);
        using var replay = PayjoinMethods.ReplaySenderEventLog(persister);
        using var state = replay.State();

        switch (state)
        {
            case SendSession.WithReplyKey withReplyKey:
                await PostOriginalAsync(session, withReplyKey.Inner, persister, cancellationToken).ConfigureAwait(false);
                break;
            case SendSession.PollingForProposal polling:
                await PollForProposalAsync(session, polling.Inner, persister, network, cancellationToken).ConfigureAwait(false);
                break;
            case SendSession.SenderPendingFallback pendingFallback:
                await BroadcastFallbackAsync(session, pendingFallback.Inner, persister, network, cancellationToken).ConfigureAwait(false);
                break;
            case SendSession.Closed closed:
                await ProcessClosedSessionAsync(session, closed.Inner, network, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task PostOriginalAsync(
        PayjoinSenderSessionState session,
        WithReplyKey sender,
        JsonSenderSessionPersister persister,
        CancellationToken cancellationToken)
    {
        var relayResponse = await _relayRequestSender.SendAsync(
            session.StoreId,
            session.SenderSessionId,
            relay => sender.CreateV2PostRequest(relay),
            DescribeRelayRequest,
            cancellationToken).ConfigureAwait(false);

        // The context is a native handle; the relay call hands it over on success, so this
        // caller owns its disposal.
        using var requestContext = relayResponse.RequestContext;
        using var transition = sender.ProcessResponse(relayResponse.ResponseBody, requestContext.OhttpCtx);
        using var polling = transition.Save(persister);
    }

    private async Task PollForProposalAsync(
        PayjoinSenderSessionState session,
        PollingForProposal polling,
        JsonSenderSessionPersister persister,
        BTCPayNetwork network,
        CancellationToken cancellationToken)
    {
        var relayResponse = await _relayRequestSender.SendAsync(
            session.StoreId,
            session.SenderSessionId,
            relay => polling.CreatePollRequest(relay),
            DescribeRelayRequest,
            cancellationToken).ConfigureAwait(false);

        using var requestContext = relayResponse.RequestContext;
        using var transition = polling.ProcessResponse(relayResponse.ResponseBody, requestContext.OhttpCtx);
        using var outcome = transition.Save(persister);
        if (outcome is not PollingForProposalTransitionOutcome.Progress progress)
        {
            return;
        }

        await FinishProposalAsync(session, progress.PsbtBase64, network, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes the proposal the library validated and gets it to the network. A wallet the server
    /// can sign for finishes here; any other wallet needs a second signature, so the proposal
    /// goes to BTCPay's pending transactions and the session waits again.
    ///
    /// The library closes the sender session as soon as it hands over a valid proposal, so this
    /// step is the caller's alone. A run that ends between the two replays into a closed session
    /// and comes straight back here with the same proposal.
    /// </summary>
    private async Task FinishProposalAsync(
        PayjoinSenderSessionState session,
        string proposalPsbtBase64,
        BTCPayNetwork network,
        CancellationToken cancellationToken)
    {
        // The library already validated the proposal against the original during
        // ProcessResponse. The proposal arrives without HD keypaths (the receiver's
        // prepare_psbt strips them, and the original this sender handed over was finalized, so
        // the library had none of ours left to restore), so NBXplorer first restores the wallet
        // metadata for our inputs; SignAll then signs only inputs that match the store's
        // derivation scheme. The receiver's own input is finalized before the library accepts
        // the proposal, so it is never signed here.
        var proposalPsbt = PSBT.Parse(proposalPsbtBase64, network.NBitcoinNetwork);
        var (derivationScheme, signer) = await ResolveSigningContextAsync(session.StoreId, network, cancellationToken).ConfigureAwait(false);
        var explorerClient = _explorerClientProvider.GetExplorerClient(network);
        var updateResponse = await explorerClient.UpdatePSBTAsync(
            new NBXplorer.Models.UpdatePSBTRequest
            {
                PSBT = proposalPsbt,
                DerivationScheme = derivationScheme.AccountDerivation
            },
            cancellationToken).ConfigureAwait(false);
        if (updateResponse?.PSBT is not null)
        {
            proposalPsbt = updateResponse.PSBT;
        }

        if (signer is null)
        {
            // The proposal is a different transaction from the original, so a wallet that
            // cannot sign on the server has to sign a second time. Park the session on a new
            // pending transaction and stop; the signature listener finishes the broadcast. If
            // the operator never signs, the library moves the session to its fallback state and
            // the original goes out instead, so the payment still completes.
            if (!RequestBaseUrl.TryFromUrl(session.RequestBaseUrl ?? string.Empty, out var requestBaseUrl))
            {
                throw new InvalidOperationException(
                    "the session recorded no base URL, so the second signing round cannot start");
            }

            var pending = await _pendingTransactionService.CreatePendingTransaction(
                session.StoreId,
                PayjoinConstants.BitcoinCode,
                proposalPsbt,
                requestBaseUrl,
                // The same window round one gets. When it lapses the sweep ends the session, and
                // because the original is signed by then, the plain payment goes out instead.
                expiry: DateTimeOffset.UtcNow + PayjoinSenderService.SignatureWindow,
                cancellationToken).ConfigureAwait(false);

            if (_senderSessionStore.AwaitSignature(session.SenderSessionId, pending.Id))
            {
                LogProposalAwaitingSignature(_logger, session.SenderSessionId, pending.Id, null);
            }
            else
            {
                // The session ended while the request was being created, so nothing waits on it.
                // Withdraw it, or the operator would be asked to sign for a dead session.
                await _pendingTransactionService.CancelPendingTransaction(
                    new PendingTransactionService.PendingTransactionFullId(
                        PayjoinConstants.BitcoinCode,
                        session.StoreId,
                        pending.Id)).ConfigureAwait(false);
            }

            return;
        }

        proposalPsbt = proposalPsbt.SignAll(derivationScheme.AccountDerivation, signer.AccountKey, signer.RootedKeyPath);
        if (!proposalPsbt.TryFinalize(out var errors))
        {
            throw new InvalidOperationException($"the payjoin proposal could not be finalized: {string.Join("; ", errors.Select(e => e.ToString()))}");
        }

        var payjoinTransaction = proposalPsbt.ExtractTransaction();
        try
        {
            await BroadcastAsync(network, payjoinTransaction, cancellationToken).ConfigureAwait(false);
        }
        catch (PayjoinSenderBroadcastException ex) when (ex.Permanent)
        {
            // The payjoin can never be accepted, most likely because the receiver's input was
            // spent in the meantime. The payment still happens: the original spends only this
            // wallet's coins.
            await FailSessionAsync(session, $"the payjoin transaction was refused: {ex.Message}", ex, cancellationToken).ConfigureAwait(false);
            return;
        }

        var payjoinTxId = payjoinTransaction.GetHash().ToString();
        LogSenderSessionBroadcast(_logger, session.SenderSessionId, payjoinTxId, null);
        _senderSessionStore.CompleteSession(
            session.SenderSessionId,
            PayjoinSenderSessionStatus.CompletedPayjoin,
            payjoinTxId,
            failureMessage: null);
        await PayjoinSenderSessionResourceReleaser
            .ReleaseAsync(_pendingTransactionService, _senderSessionStore, session).ConfigureAwait(false);
    }

    private async Task BroadcastFallbackAsync(
        PayjoinSenderSessionState session,
        SenderPendingFallback pendingFallback,
        JsonSenderSessionPersister persister,
        BTCPayNetwork network,
        CancellationToken cancellationToken)
    {
        // The library moved the session here: the payjoin round is over (expiry or
        // cancellation), and the payment must still happen. Broadcast the original
        // transaction, then close the session through the library so the event log
        // records the handoff.
        var fallbackTransaction = Transaction.Load(pendingFallback.FallbackTx(), network.NBitcoinNetwork);
        try
        {
            await BroadcastAsync(network, fallbackTransaction, cancellationToken).ConfigureAwait(false);
        }
        catch (PayjoinSenderBroadcastException ex) when (ex.Permanent)
        {
            // The fallback can never be accepted: something else spent the coins. End the
            // session; the terminator hits the same refusal and records the failure.
            await FailSessionAsync(session, $"the plain payment was refused: {ex.Message}", ex, cancellationToken).ConfigureAwait(false);
            return;
        }

        using var transition = pendingFallback.Close();
        transition.Save(persister);

        var fallbackTxId = fallbackTransaction.GetHash().ToString();
        LogSenderSessionBroadcast(_logger, session.SenderSessionId, fallbackTxId, null);
        _senderSessionStore.CompleteSession(
            session.SenderSessionId,
            PayjoinSenderSessionStatus.CompletedFallback,
            fallbackTxId,
            failureMessage: null);
        await PayjoinSenderSessionResourceReleaser
            .ReleaseAsync(_pendingTransactionService, _senderSessionStore, session).ConfigureAwait(false);
    }

    private static (SystemUri Url, string ContentType, byte[] Body) DescribeRelayRequest(
        RequestOhttpContext requestContext) =>
        (
            new SystemUri(requestContext.Request.Url, UriKind.Absolute),
            requestContext.Request.ContentType,
            requestContext.Request.Body
        );

    /// <summary>
    /// Returns the derivation scheme, and the account key only when the server holds one. A null
    /// signer is the normal answer for a cold wallet, a hardware device or a multisig group, and
    /// it sends the proposal to BTCPay's pending transactions to be signed there.
    /// </summary>
    private async Task<(DerivationSchemeSettings DerivationScheme, SenderSigner? Signer)> ResolveSigningContextAsync(
        string storeId,
        BTCPayNetwork network,
        CancellationToken cancellationToken)
    {
        var store = await _storeRepository.FindStore(storeId).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"store {storeId} not found");
        var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(PayjoinConstants.BitcoinCode);
        var derivationScheme = store.GetPaymentMethodConfig<DerivationSchemeSettings>(paymentMethodId, _handlers, true)
            ?? throw new InvalidOperationException("derivation scheme not configured for BTC");
        if (!derivationScheme.IsHotWallet)
        {
            return (derivationScheme, null);
        }

        var explorerClient = _explorerClientProvider.GetExplorerClient(network);
        var signingKeyStr = await explorerClient.GetMetadataAsync<string>(
            derivationScheme.AccountDerivation,
            WellknownMetadataKeys.MasterHDKey,
            cancellationToken).ConfigureAwait(false);
        if (signingKeyStr is null)
        {
            return (derivationScheme, null);
        }

        var signingKey = ExtKey.Parse(signingKeyStr, network.NBitcoinNetwork);
        var rootedKeyPath = derivationScheme.GetAccountKeySettingsFromRoot(signingKey)?.GetRootedKeyPath();
        if (rootedKeyPath is null)
        {
            return (derivationScheme, null);
        }

        return (derivationScheme, new SenderSigner(signingKey.Derive(rootedKeyPath.KeyPath), rootedKeyPath));
    }

    private sealed record SenderSigner(ExtKey AccountKey, RootedKeyPath RootedKeyPath);

    private async Task BroadcastAsync(BTCPayNetwork network, Transaction transaction, CancellationToken cancellationToken)
    {
        var explorerClient = _explorerClientProvider.GetExplorerClient(network);
        await PayjoinSenderBroadcaster.BroadcastAsync(explorerClient, transaction, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deals with a session the library has closed while this store still holds it open. A
    /// success means the library handed over a valid proposal and stopped there, so the payjoin
    /// still has to be signed and broadcast; this is the path a run that ended mid-proposal takes
    /// when it replays. Any other outcome ends the session.
    /// </summary>
    private async Task ProcessClosedSessionAsync(
        PayjoinSenderSessionState session,
        SenderSessionOutcome outcome,
        BTCPayNetwork network,
        CancellationToken cancellationToken)
    {
        var proposalPsbtBase64 = outcome.IsSuccess() ? outcome.SuccessPsbtBase64() : null;
        if (proposalPsbtBase64 is not null)
        {
            await FinishProposalAsync(session, proposalPsbtBase64, network, cancellationToken).ConfigureAwait(false);
            return;
        }

        // The payjoin round is over without a proposal. The payment is not: when the original
        // is signed it goes out as the fallback, which also covers a run that crashed between
        // its fallback broadcast and its bookkeeping, because broadcasting again is idempotent.
        await FailSessionAsync(
            session,
            "the sender session closed without a payjoin",
            exception: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Ends a session the plugin can no longer drive, through the shared terminator: the
    /// signed original goes out when it exists, and a fallback the node refuses transiently
    /// leaves the session in place for the next tick to retry.
    /// </summary>
    private async Task FailSessionAsync(
        PayjoinSenderSessionState session,
        string message,
        Exception? exception,
        CancellationToken cancellationToken)
    {
        LogSenderSessionFailed(_logger, session.SenderSessionId, exception);
        var network = _networkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode);
        if (network is null)
        {
            _senderSessionStore.CompleteSession(
                session.SenderSessionId,
                PayjoinSenderSessionStatus.Failed,
                broadcastTransactionId: null,
                message);
            await PayjoinSenderSessionResourceReleaser
                .ReleaseAsync(_pendingTransactionService, _senderSessionStore, session).ConfigureAwait(false);
            return;
        }

        var outcome = await PayjoinSenderSessionTerminator.EndAsync(
            _senderSessionStore,
            _pendingTransactionService,
            _explorerClientProvider.GetExplorerClient(network),
            network.NBitcoinNetwork,
            session,
            message,
            cancellationToken).ConfigureAwait(false);
        if (outcome == PayjoinSenderTerminalOutcome.FallbackBroadcast)
        {
            LogSenderSessionBroadcast(_logger, session.SenderSessionId, session.OriginalTransactionId, null);
        }
        else if (outcome == PayjoinSenderTerminalOutcome.RetryLater)
        {
            LogSenderSessionTransient(_logger, session.SenderSessionId, null);
        }
    }
}
