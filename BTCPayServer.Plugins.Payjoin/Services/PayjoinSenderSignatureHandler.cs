using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Plugins.Payjoin.Data;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer;
using Payjoin;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

/// <summary>
/// Takes a signature collected off the server and moves the payjoin session on. Two rounds arrive
/// here: the signed original, which starts the session, and the signed proposal, which completes
/// it.
///
/// Two callers drive this. A listener reacts to BTCPay's signature event, which is the fast path,
/// and the poller sweeps every waiting session on each tick, which is the reliable one. The event
/// travels in memory only, so a restart can drop it, and a pending transaction that the operator
/// cancels or that expires produces no event this plugin can use at all. The sweep is therefore
/// the path that must be correct; the listener only makes it prompt.
/// </summary>
internal sealed class PayjoinSenderSignatureHandler
{
    private static readonly Action<ILogger, string, Exception?> LogSessionStarted =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(1, nameof(LogSessionStarted)),
            "Payjoin sender session {SenderSessionId} received its signed original and is now live");
    private static readonly Action<ILogger, string, string, Exception?> LogProposalBroadcast =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(2, nameof(LogProposalBroadcast)),
            "Payjoin sender session {SenderSessionId} broadcast the signed proposal {TransactionId}");
    private static readonly Action<ILogger, string, Exception?> LogSignatureHandlingFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(3, nameof(LogSignatureHandlingFailed)),
            "Payjoin sender session {SenderSessionId} could not use the collected signature");
    private static readonly Action<ILogger, string, Exception?> LogBroadcastRefused =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(5, nameof(LogBroadcastRefused)),
            "Payjoin sender session {SenderSessionId} could not broadcast; it retries next tick");
    private static readonly Action<ILogger, string, string, Exception?> LogSessionAbandoned =
        LoggerMessage.Define<string, string>(LogLevel.Warning, new EventId(4, nameof(LogSessionAbandoned)),
            "Payjoin sender session {SenderSessionId} will never be signed: {Reason}");

    private readonly PayjoinSenderSessionStore _senderSessionStore;
    private readonly PendingTransactionService _pendingTransactionService;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly ExplorerClientProvider _explorerClientProvider;
    private readonly ILogger<PayjoinSenderSignatureHandler> _logger;

    internal PayjoinSenderSignatureHandler(
        PayjoinSenderSessionStore senderSessionStore,
        PendingTransactionService pendingTransactionService,
        BTCPayNetworkProvider networkProvider,
        ExplorerClientProvider explorerClientProvider,
        ILogger<PayjoinSenderSignatureHandler> logger)
    {
        _senderSessionStore = senderSessionStore;
        _pendingTransactionService = pendingTransactionService;
        _networkProvider = networkProvider;
        _explorerClientProvider = explorerClientProvider;
        _logger = logger;
    }

    /// <summary>
    /// Brings every waiting session back in line with the pending transaction it waits on. This
    /// recovers a signature whose event was lost, and it ends a session whose transaction the
    /// operator cancelled or let expire, instead of leaving it to wait for ever.
    /// </summary>
    internal async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        foreach (var session in _senderSessionStore.GetSessionsAwaitingSignature())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ReconcileSessionAsync(session, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is InvalidOperationException or UniffiException or FormatException
                                       or PayjoinSenderBroadcastException or System.Net.Http.HttpRequestException
                                       or Microsoft.EntityFrameworkCore.DbUpdateException)
            {
                // One session must not stop the sweep: the others are waiting on their own
                // transactions and have nothing to do with this failure. DbUpdateException is the
                // benign end of the listener-versus-sweep race: the unique event-sequence index
                // let exactly one of them seed the session, and this run lost.
                LogSignatureHandlingFailed(_logger, session.SenderSessionId, ex);
            }
        }

        foreach (var session in _senderSessionStore.GetPendingSessionsWithCoinReservations())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ReconcileReservationAsync(session, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is InvalidOperationException or UniffiException or FormatException
                                       or PayjoinSenderBroadcastException or System.Net.Http.HttpRequestException
                                       or Microsoft.EntityFrameworkCore.DbUpdateException)
            {
                LogSignatureHandlingFailed(_logger, session.SenderSessionId, ex);
            }
        }

        // A run that crashed between completing a session and releasing its reservation left
        // the reservation holding coins for a session that is over. Finish those releases here.
        foreach (var session in _senderSessionStore.GetSessionsWithDanglingCoinReservations())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await PayjoinSenderCoinReservationReleaser
                    .ReleaseAsync(_pendingTransactionService, _senderSessionStore, session).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is InvalidOperationException
                                       or Microsoft.EntityFrameworkCore.DbUpdateException)
            {
                LogSignatureHandlingFailed(_logger, session.SenderSessionId, ex);
            }
        }
    }

    /// <summary>
    /// Follows what the operator does to a session's coin reservation on BTCPay's own screen.
    /// The reservation is the signed plain payment, so a manual broadcast there is the fallback
    /// happening by hand, and a cancellation carries the same intent as this plugin's own stop
    /// control: end the payjoin, keep the payment. The payment cannot be taken back either way,
    /// because the receiver already holds the signed original.
    /// </summary>
    private async Task ReconcileReservationAsync(PayjoinSenderSessionState session, CancellationToken cancellationToken)
    {
        if (session.CoinReservationTransactionId is null)
        {
            return;
        }

        var reservation = await _pendingTransactionService.GetPendingTransaction(
            new PendingTransactionService.PendingTransactionFullId(
                PayjoinConstants.BitcoinCode,
                session.StoreId,
                session.CoinReservationTransactionId)).ConfigureAwait(false);
        if (reservation is null)
        {
            return;
        }

        switch (reservation.State)
        {
            case PendingTransactionState.Broadcast:
                // The operator broadcast the plain payment by hand, so the payjoin is over and
                // the fallback has happened.
                PayjoinSenderSessionCloser.TryClose(_senderSessionStore.CreatePersister(session.SenderSessionId));
                _senderSessionStore.CompleteSession(
                    session.SenderSessionId,
                    PayjoinSenderSessionStatus.CompletedFallback,
                    reservation.TransactionId ?? reservation.NoSignatureTransactionId,
                    failureMessage: null);
                _senderSessionStore.ClearCoinReservation(session.SenderSessionId);
                break;
            case PendingTransactionState.Cancelled:
            case PendingTransactionState.Expired:
                await EndSessionAsync(session, "the operator cancelled the reserved payment", cancellationToken).ConfigureAwait(false);
                break;
            case PendingTransactionState.Invalidated:
                // Something else spent the coins. When that something is this session's own
                // payjoin, the session has already completed and the terminal guard makes this
                // a no-op; a genuine outside spend ends the session with nothing to broadcast.
                _senderSessionStore.CompleteSession(
                    session.SenderSessionId,
                    PayjoinSenderSessionStatus.Failed,
                    broadcastTransactionId: null,
                    "another transaction spent the coins this session committed");
                _senderSessionStore.ClearCoinReservation(session.SenderSessionId);
                break;
            case PendingTransactionState.Pending:
            case PendingTransactionState.Signed:
                break;
        }
    }

    private async Task ReconcileSessionAsync(PayjoinSenderSessionState session, CancellationToken cancellationToken)
    {
        {
            if (session.PendingTransactionId is null)
            {
                await EndSessionAsync(session, "the session records no pending transaction", cancellationToken).ConfigureAwait(false);
                return;
            }

            var pendingTransaction = await _pendingTransactionService.GetPendingTransaction(
                new PendingTransactionService.PendingTransactionFullId(
                    PayjoinConstants.BitcoinCode,
                    session.StoreId,
                    session.PendingTransactionId)).ConfigureAwait(false);
            if (pendingTransaction is null)
            {
                await EndSessionAsync(session, "the pending transaction no longer exists", cancellationToken).ConfigureAwait(false);
                return;
            }

            switch (pendingTransaction.State)
            {
                case PendingTransactionState.Signed:
                    await HandleSignedAsync(session, pendingTransaction, cancellationToken).ConfigureAwait(false);
                    break;
                case PendingTransactionState.Broadcast:
                    // Someone broadcast it from BTCPay's own screen. Which transaction that was
                    // decides the outcome: the original is the fallback, and the proposal is the
                    // payjoin itself.
                    await CompleteFromExternalBroadcastAsync(session, pendingTransaction).ConfigureAwait(false);
                    break;
                case PendingTransactionState.Cancelled:
                    await EndSessionAsync(session, "the operator cancelled the transaction", cancellationToken).ConfigureAwait(false);
                    break;
                case PendingTransactionState.Expired:
                    await EndSessionAsync(session, "the transaction expired before it was signed", cancellationToken).ConfigureAwait(false);
                    break;
                case PendingTransactionState.Invalidated:
                    await EndSessionAsync(session, "another transaction spent the same coins", cancellationToken).ConfigureAwait(false);
                    break;
                case PendingTransactionState.Pending:
                    // Core stamps the expiry on the row but its own sweep never marks rows
                    // Expired: the sweep lives on an event loop core never starts. Enforce the
                    // window here, or a forgotten signing request holds its coins for ever.
                    if (pendingTransaction.Expiry is { } expiry && expiry <= DateTimeOffset.UtcNow)
                    {
                        await _pendingTransactionService.CancelPendingTransaction(
                            new PendingTransactionService.PendingTransactionFullId(
                                PayjoinConstants.BitcoinCode,
                                session.StoreId,
                                session.PendingTransactionId!)).ConfigureAwait(false);
                        await EndSessionAsync(session, "the transaction expired before it was signed", cancellationToken).ConfigureAwait(false);
                    }

                    break;
            }
        }
    }

    internal async Task HandleSignedAsync(
        PayjoinSenderSessionState session,
        PendingTransaction pendingTransaction,
        CancellationToken cancellationToken)
    {
        // Read the session again: the operator may have stopped it between the signature being
        // collected and this running, and a stopped session has already broadcast its original.
        if (!_senderSessionStore.TryGetSession(session.SenderSessionId, out var current) ||
            current is null ||
            current.Status != PayjoinSenderSessionStatus.AwaitingSignature)
        {
            return;
        }

        try
        {
            var network = _networkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode)
                ?? throw new InvalidOperationException("BTC network not available");
            var signedPsbt = LoadSignedPsbt(pendingTransaction, network.NBitcoinNetwork);

            if (current.Events.Length == 0)
            {
                StartSession(current, signedPsbt);
            }
            else
            {
                await BroadcastProposalAsync(current, signedPsbt, network, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (PayjoinSenderBroadcastException ex)
        {
            // The transaction is signed and valid as far as this plugin can tell, so the node
            // refusing it now is not a reason to throw the payjoin away. The session stays where
            // it is and the next sweep tries again.
            LogBroadcastRefused(_logger, session.SenderSessionId, ex);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UniffiException or FormatException)
        {
            LogSignatureHandlingFailed(_logger, session.SenderSessionId, ex);
            await EndSessionAsync(session, ex.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The first round: the signed original exists, so the rust-payjoin sender can finally be
    /// built. Saving its state moves the session to Pending and the poller takes over.
    /// </summary>
    private void StartSession(PayjoinSenderSessionState session, PSBT signedPsbt)
    {
        // rust-payjoin needs the original complete, because it broadcasts it as the fallback.
        if (!signedPsbt.TryFinalize(out var errors))
        {
            throw new InvalidOperationException($"the signed original could not be finalized: {string.Join("; ", errors.Select(e => e.ToString()))}");
        }

        var bootstrapPersister = new CapturingSenderSessionPersister();
        using (var uri = global::Payjoin.Uri.Parse(session.Bip21))
        using (var pjUri = uri.CheckPjSupported())
        using (var senderBuilder = new SenderBuilder(signedPsbt.ToBase64(), pjUri))
        using (var transition = senderBuilder.BuildRecommended(PayjoinSenderService.ResolveMinFeeRate(session.FeeRateSatPerKwu)))
        {
            using var sender = transition.Save(bootstrapPersister);
        }

        if (!_senderSessionStore.StartSignedSession(
                session.SenderSessionId,
                bootstrapPersister.Load(),
                signedPsbt.ExtractTransaction().ToHex()))
        {
            return;
        }

        // The signed row stays open on purpose: the store just made it the session's coin
        // reservation, so core keeps its outpoints away from ordinary sends, and its broadcast
        // button doubles as the operator's manual fallback. The session's terminal transition
        // releases the row.
        LogSessionStarted(_logger, session.SenderSessionId, null);
    }

    /// <summary>
    /// The second round: the receiver's proposal came back signed, so it can go to the network.
    /// </summary>
    private async Task BroadcastProposalAsync(
        PayjoinSenderSessionState session,
        PSBT signedPsbt,
        BTCPayNetwork network,
        CancellationToken cancellationToken)
    {
        if (!signedPsbt.TryFinalize(out var errors))
        {
            throw new InvalidOperationException($"the signed proposal could not be finalized: {string.Join("; ", errors.Select(e => e.ToString()))}");
        }

        var transaction = signedPsbt.ExtractTransaction();
        var explorerClient = _explorerClientProvider.GetExplorerClient(network);
        var transactionId = await PayjoinSenderBroadcaster
            .BroadcastAsync(explorerClient, transaction, cancellationToken)
            .ConfigureAwait(false);

        LogProposalBroadcast(_logger, session.SenderSessionId, transactionId, null);
        _senderSessionStore.CompleteSession(
            session.SenderSessionId,
            PayjoinSenderSessionStatus.CompletedPayjoin,
            transactionId,
            failureMessage: null);
        await PayjoinSenderCoinReservationReleaser
            .ReleaseAsync(_pendingTransactionService, _senderSessionStore, session).ConfigureAwait(false);
    }

    private async Task CompleteFromExternalBroadcastAsync(PayjoinSenderSessionState session, PendingTransaction pendingTransaction)
    {
        var isProposal = session.Events.Length > 0;
        _senderSessionStore.CompleteSession(
            session.SenderSessionId,
            isProposal ? PayjoinSenderSessionStatus.CompletedPayjoin : PayjoinSenderSessionStatus.CompletedFallback,
            pendingTransaction.TransactionId ?? pendingTransaction.NoSignatureTransactionId,
            failureMessage: null);
        await PayjoinSenderCoinReservationReleaser
            .ReleaseAsync(_pendingTransactionService, _senderSessionStore, session).ConfigureAwait(false);
    }

    /// <summary>
    /// Ends a session that will never get the signature it waits for. The payjoin is over either
    /// way, but the payment is not: once the original is signed the operator has authorised it,
    /// so it goes to the network as a plain transaction rather than being thrown away. Only a
    /// session whose original was never signed ends with nothing broadcast.
    /// </summary>
    private async Task EndSessionAsync(PayjoinSenderSessionState session, string reason, CancellationToken cancellationToken)
    {
        LogSessionAbandoned(_logger, session.SenderSessionId, reason, null);
        if (session.OriginalTransactionHex is null)
        {
            _senderSessionStore.CompleteSession(
                session.SenderSessionId,
                PayjoinSenderSessionStatus.Failed,
                broadcastTransactionId: null,
                reason);
            await PayjoinSenderCoinReservationReleaser
                .ReleaseAsync(_pendingTransactionService, _senderSessionStore, session).ConfigureAwait(false);
            return;
        }

        var network = _networkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode)
            ?? throw new InvalidOperationException("BTC network not available");
        var fallbackTransaction = Transaction.Parse(session.OriginalTransactionHex, network.NBitcoinNetwork);
        var explorerClient = _explorerClientProvider.GetExplorerClient(network);
        var transactionId = await PayjoinSenderBroadcaster
            .BroadcastAsync(explorerClient, fallbackTransaction, cancellationToken)
            .ConfigureAwait(false);

        PayjoinSenderSessionCloser.TryClose(_senderSessionStore.CreatePersister(session.SenderSessionId));
        _senderSessionStore.CompleteSession(
            session.SenderSessionId,
            PayjoinSenderSessionStatus.CompletedFallback,
            transactionId,
            failureMessage: reason);
        await PayjoinSenderCoinReservationReleaser
            .ReleaseAsync(_pendingTransactionService, _senderSessionStore, session).ConfigureAwait(false);
    }

    /// <summary>
    /// Rebuilds the effective PSBT the way BTCPay's own pending-transaction screen does: start
    /// from the stored PSBT and combine every collected signature onto it.
    /// </summary>
    private static PSBT LoadSignedPsbt(PendingTransaction pendingTransaction, Network network)
    {
        var blob = pendingTransaction.GetBlob()
            ?? throw new InvalidOperationException("the pending transaction carries no data");
        var psbt = PSBT.Parse(blob.PSBT, network);
        foreach (var collected in blob.CollectedSignatures)
        {
            psbt = psbt.Combine(PSBT.Parse(collected.ReceivedPSBT, network));
        }

        return psbt;
    }
}
