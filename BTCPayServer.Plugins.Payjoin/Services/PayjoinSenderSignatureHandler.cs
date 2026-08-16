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
            if (session.PendingTransactionId is null)
            {
                Abandon(session, "the session records no pending transaction");
                continue;
            }

            var pendingTransaction = await _pendingTransactionService.GetPendingTransaction(
                new PendingTransactionService.PendingTransactionFullId(
                    PayjoinConstants.BitcoinCode,
                    session.StoreId,
                    session.PendingTransactionId)).ConfigureAwait(false);
            if (pendingTransaction is null)
            {
                Abandon(session, "the pending transaction no longer exists");
                continue;
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
                    CompleteFromExternalBroadcast(session, pendingTransaction);
                    break;
                case PendingTransactionState.Cancelled:
                    Abandon(session, "the operator cancelled the transaction");
                    break;
                case PendingTransactionState.Expired:
                    Abandon(session, "the transaction expired before it was signed");
                    break;
                case PendingTransactionState.Invalidated:
                    Abandon(session, "another transaction spent the same coins");
                    break;
                case PendingTransactionState.Pending:
                    break;
            }
        }
    }

    internal async Task HandleSignedAsync(
        PayjoinSenderSessionState session,
        PendingTransaction pendingTransaction,
        CancellationToken cancellationToken)
    {
        try
        {
            var network = _networkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode)
                ?? throw new InvalidOperationException("BTC network not available");
            var signedPsbt = LoadSignedPsbt(pendingTransaction, network.NBitcoinNetwork);

            if (session.Events.Length == 0)
            {
                StartSession(session, signedPsbt);
            }
            else
            {
                await BroadcastProposalAsync(session, signedPsbt, network, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or UniffiException or FormatException)
        {
            LogSignatureHandlingFailed(_logger, session.SenderSessionId, ex);
            _senderSessionStore.CompleteSession(
                session.SenderSessionId,
                PayjoinSenderSessionStatus.Failed,
                broadcastTransactionId: null,
                ex.Message);
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

        if (_senderSessionStore.StartSignedSession(
                session.SenderSessionId,
                bootstrapPersister.Load(),
                signedPsbt.ExtractTransaction().ToHex()))
        {
            LogSessionStarted(_logger, session.SenderSessionId, null);
        }
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
    }

    private void CompleteFromExternalBroadcast(PayjoinSenderSessionState session, PendingTransaction pendingTransaction)
    {
        var isProposal = session.Events.Length > 0;
        _senderSessionStore.CompleteSession(
            session.SenderSessionId,
            isProposal ? PayjoinSenderSessionStatus.CompletedPayjoin : PayjoinSenderSessionStatus.CompletedFallback,
            pendingTransaction.TransactionId ?? pendingTransaction.NoSignatureTransactionId,
            failureMessage: null);
    }

    private void Abandon(PayjoinSenderSessionState session, string reason)
    {
        LogSessionAbandoned(_logger, session.SenderSessionId, reason, null);
        _senderSessionStore.CompleteSession(
            session.SenderSessionId,
            PayjoinSenderSessionStatus.Failed,
            broadcastTransactionId: null,
            reason);
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
