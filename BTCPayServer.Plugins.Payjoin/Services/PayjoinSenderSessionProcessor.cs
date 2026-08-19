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
    private static readonly Action<ILogger, string, Exception?> LogSenderRelayUnavailable =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(4, nameof(LogSenderRelayUnavailable)),
            "Payjoin sender session {SenderSessionId} has no reachable OHTTP relay; it retries next tick");

    private readonly PayjoinSenderSessionStore _senderSessionStore;
    private readonly IPayjoinStoreSettingsRepository _storeSettingsRepository;
    private readonly IPayjoinReceiverRelayClient _relayClient;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly StoreRepository _storeRepository;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly ExplorerClientProvider _explorerClientProvider;
    private readonly PendingTransactionService _pendingTransactionService;
    private readonly PayjoinSenderSignatureHandler _signatureHandler;
    private readonly ILogger<PayjoinSenderSessionProcessor> _logger;

    internal PayjoinSenderSessionProcessor(
        PayjoinSenderSessionStore senderSessionStore,
        IPayjoinStoreSettingsRepository storeSettingsRepository,
        IPayjoinReceiverRelayClient relayClient,
        BTCPayNetworkProvider networkProvider,
        StoreRepository storeRepository,
        PaymentMethodHandlerDictionary handlers,
        ExplorerClientProvider explorerClientProvider,
        PendingTransactionService pendingTransactionService,
        PayjoinSenderSignatureHandler signatureHandler,
        ILogger<PayjoinSenderSessionProcessor> logger)
    {
        _senderSessionStore = senderSessionStore;
        _storeSettingsRepository = storeSettingsRepository;
        _relayClient = relayClient;
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

        foreach (var session in _senderSessionStore.GetPendingSessions())
        {
            stoppingToken.ThrowIfCancellationRequested();
            try
            {
                await ProcessSessionAsync(session, stoppingToken).ConfigureAwait(false);
            }
            catch (PayjoinReceiverRelayTimeoutException ex)
            {
                // A stalled relay is this session's problem, not the tick's. It derives from
                // TaskCanceledException, so without this it would leave the loop as a
                // cancellation and every session after it would be skipped.
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

        // A session with no signed original has nothing to broadcast: the coins were never
        // committed to the network, so the payment simply does not happen.
        if (session.OriginalTransactionHex is null)
        {
            _senderSessionStore.CompleteSession(
                session.SenderSessionId,
                PayjoinSenderSessionStatus.Failed,
                broadcastTransactionId: null,
                "the operator cancelled the payment before it was signed");
            await PayjoinSenderCoinReservationReleaser
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
        await PayjoinSenderCoinReservationReleaser
            .ReleaseAsync(_pendingTransactionService, _senderSessionStore, session).ConfigureAwait(false);
        return PayjoinSenderCancelResult.Broadcast(fallbackTxId);
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
        var responseBody = await SendThroughRelayAsync(
            session,
            relay => sender.CreateV2PostRequest(relay),
            cancellationToken).ConfigureAwait(false);
        if (responseBody is null)
        {
            return;
        }

        // The context is a native handle; the relay call hands it over on success, so this
        // caller owns its disposal.
        using var requestContext = responseBody.Value.Context;
        using var transition = sender.ProcessResponse(responseBody.Value.Body, requestContext.OhttpCtx);
        using var polling = transition.Save(persister);
    }

    private async Task PollForProposalAsync(
        PayjoinSenderSessionState session,
        PollingForProposal polling,
        JsonSenderSessionPersister persister,
        BTCPayNetwork network,
        CancellationToken cancellationToken)
    {
        var responseBody = await SendThroughRelayAsync(
            session,
            relay => polling.CreatePollRequest(relay),
            cancellationToken).ConfigureAwait(false);
        if (responseBody is null)
        {
            return;
        }

        using var requestContext = responseBody.Value.Context;
        using var transition = polling.ProcessResponse(responseBody.Value.Body, requestContext.OhttpCtx);
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
        await BroadcastAsync(network, payjoinTransaction, cancellationToken).ConfigureAwait(false);

        var payjoinTxId = payjoinTransaction.GetHash().ToString();
        LogSenderSessionBroadcast(_logger, session.SenderSessionId, payjoinTxId, null);
        _senderSessionStore.CompleteSession(
            session.SenderSessionId,
            PayjoinSenderSessionStatus.CompletedPayjoin,
            payjoinTxId,
            failureMessage: null);
        await PayjoinSenderCoinReservationReleaser
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
        await BroadcastAsync(network, fallbackTransaction, cancellationToken).ConfigureAwait(false);

        using var transition = pendingFallback.Close();
        transition.Save(persister);

        var fallbackTxId = fallbackTransaction.GetHash().ToString();
        LogSenderSessionBroadcast(_logger, session.SenderSessionId, fallbackTxId, null);
        _senderSessionStore.CompleteSession(
            session.SenderSessionId,
            PayjoinSenderSessionStatus.CompletedFallback,
            fallbackTxId,
            failureMessage: null);
        await PayjoinSenderCoinReservationReleaser
            .ReleaseAsync(_pendingTransactionService, _senderSessionStore, session).ConfigureAwait(false);
    }

    private async Task<(byte[] Body, RequestOhttpContext Context)?> SendThroughRelayAsync(
        PayjoinSenderSessionState session,
        Func<string, RequestOhttpContext> buildRequest,
        CancellationToken cancellationToken)
    {
        var storeSettings = await _storeSettingsRepository.GetAsync(session.StoreId).ConfigureAwait(false);
        var relayUrls = storeSettings?.GetEffectiveOhttpRelayUrls() ?? [];
        Exception? lastError = null;
        foreach (var relayUrl in relayUrls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestContext = buildRequest(relayUrl.AbsoluteUri);
            try
            {
                var body = await _relayClient.SendAsync(
                    new SystemUri(requestContext.Request.Url),
                    requestContext.Request.ContentType,
                    requestContext.Request.Body,
                    cancellationToken).ConfigureAwait(false);
                return (body, requestContext);
            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                lastError = ex;
                requestContext.Dispose();
            }
            catch (PayjoinReceiverRelayTimeoutException ex)
            {
                // A stalled relay must not stop the rotation: without this the next relay is
                // never tried, and one dead relay blocks every sender session of the store.
                lastError = ex;
                requestContext.Dispose();
            }
        }

        LogSenderRelayUnavailable(_logger, session.SenderSessionId, lastError);
        return null;
    }

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
    /// Ends a session the plugin can no longer drive. The payjoin is over, but the payment need
    /// not be: once the original is signed the operator has authorised it, and the receiver
    /// already holds a copy it could broadcast at any moment, so recording a failure while
    /// keeping the transaction off the network would only misstate what can still happen.
    /// Whenever the signed original exists it goes out and the session completes as a fallback;
    /// only a session with nothing signed ends with nothing broadcast.
    /// </summary>
    private async Task FailSessionAsync(
        PayjoinSenderSessionState session,
        string message,
        Exception? exception,
        CancellationToken cancellationToken)
    {
        LogSenderSessionFailed(_logger, session.SenderSessionId, exception);
        if (session.OriginalTransactionHex is not null)
        {
            var network = _networkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode);
            if (network is not null)
            {
                Transaction fallbackTransaction;
                try
                {
                    fallbackTransaction = Transaction.Parse(session.OriginalTransactionHex, network.NBitcoinNetwork);
                    await BroadcastAsync(network, fallbackTransaction, cancellationToken).ConfigureAwait(false);
                }
                catch (PayjoinSenderBroadcastException ex)
                {
                    // The node refused the fallback for now. Leave the session where it is: the
                    // next tick hits the same failure and retries this broadcast.
                    LogSenderSessionTransient(_logger, session.SenderSessionId, ex);
                    return;
                }
                catch (FormatException ex)
                {
                    LogSenderSessionFailed(_logger, session.SenderSessionId, ex);
                    _senderSessionStore.CompleteSession(
                        session.SenderSessionId,
                        PayjoinSenderSessionStatus.Failed,
                        broadcastTransactionId: null,
                        $"{message}; the stored fallback could not be parsed");
                    await PayjoinSenderCoinReservationReleaser
                        .ReleaseAsync(_pendingTransactionService, _senderSessionStore, session).ConfigureAwait(false);
                    return;
                }

                PayjoinSenderSessionCloser.TryClose(_senderSessionStore.CreatePersister(session.SenderSessionId));
                var fallbackTxId = fallbackTransaction.GetHash().ToString();
                LogSenderSessionBroadcast(_logger, session.SenderSessionId, fallbackTxId, null);
                _senderSessionStore.CompleteSession(
                    session.SenderSessionId,
                    PayjoinSenderSessionStatus.CompletedFallback,
                    fallbackTxId,
                    message);
                await PayjoinSenderCoinReservationReleaser
                    .ReleaseAsync(_pendingTransactionService, _senderSessionStore, session).ConfigureAwait(false);
                return;
            }
        }

        _senderSessionStore.CompleteSession(
            session.SenderSessionId,
            PayjoinSenderSessionStatus.Failed,
            broadcastTransactionId: null,
            message);
        await PayjoinSenderCoinReservationReleaser
            .ReleaseAsync(_pendingTransactionService, _senderSessionStore, session).ConfigureAwait(false);
    }
}
