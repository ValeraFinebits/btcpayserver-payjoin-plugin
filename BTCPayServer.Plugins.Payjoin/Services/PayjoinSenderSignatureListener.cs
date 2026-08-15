using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Plugins.Payjoin.Data;
using Microsoft.Extensions.Hosting;
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
/// Bridges BTCPay's pending transactions to the payjoin sender. A wallet that cannot sign on the
/// server signs through BTCPay's own screens, which support the vault, hardware devices, a seed
/// and multisig collection. Core stops there: it collects signatures and does not broadcast. This
/// listener picks the signed transaction up and hands it to rust-payjoin.
///
/// Two rounds use this path. The first is the original transaction, which starts the session. The
/// second is the receiver's proposal, which is a different transaction and so needs its own
/// signature before it can be broadcast.
/// </summary>
internal sealed class PayjoinSenderSignatureListener : IHostedService, IDisposable
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

    private readonly EventAggregator _eventAggregator;
    private readonly PayjoinSenderSessionStore _senderSessionStore;
    private readonly PendingTransactionService _pendingTransactionService;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly ExplorerClientProvider _explorerClientProvider;
    private readonly ILogger<PayjoinSenderSignatureListener> _logger;
    private IEventAggregatorSubscription? _subscription;

    internal PayjoinSenderSignatureListener(
        EventAggregator eventAggregator,
        PayjoinSenderSessionStore senderSessionStore,
        PendingTransactionService pendingTransactionService,
        BTCPayNetworkProvider networkProvider,
        ExplorerClientProvider explorerClientProvider,
        ILogger<PayjoinSenderSignatureListener> logger)
    {
        _eventAggregator = eventAggregator;
        _senderSessionStore = senderSessionStore;
        _pendingTransactionService = pendingTransactionService;
        _networkProvider = networkProvider;
        _explorerClientProvider = explorerClientProvider;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _eventAggregator.SubscribeAsync<PendingTransactionService.PendingTransactionEvent>(OnPendingTransactionEventAsync);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    public void Dispose() => _subscription?.Dispose();

    private async Task OnPendingTransactionEventAsync(PendingTransactionService.PendingTransactionEvent evt)
    {
        // Only a fully signed transaction is actionable. A partial signature on a multisig
        // wallet keeps the row Pending, and this listener waits for the next one.
        if (evt?.Data is null || evt.Data.State != PendingTransactionState.Signed)
        {
            return;
        }

        if (!_senderSessionStore.TryGetSessionByPendingTransactionId(evt.Data.Id, out var session) || session is null)
        {
            return;
        }

        try
        {
            var network = _networkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode)
                ?? throw new InvalidOperationException("BTC network not available");
            var signedPsbt = LoadSignedPsbt(evt.Data, network.NBitcoinNetwork);

            if (session.Events.Length == 0)
            {
                StartSession(session, signedPsbt);
            }
            else
            {
                await BroadcastProposalAsync(session, signedPsbt, network).ConfigureAwait(false);
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
        using (var transition = senderBuilder.BuildRecommended(PayjoinSenderService.MinFeeRateSatPerKwu))
        {
            using var sender = transition.Save(bootstrapPersister);
        }

        if (_senderSessionStore.StartSignedSession(session.SenderSessionId, bootstrapPersister.Load()))
        {
            LogSessionStarted(_logger, session.SenderSessionId, null);
        }
    }

    /// <summary>
    /// The second round: the receiver's proposal came back signed, so it can go to the network.
    /// </summary>
    private async Task BroadcastProposalAsync(PayjoinSenderSessionState session, PSBT signedPsbt, BTCPayNetwork network)
    {
        if (!signedPsbt.TryFinalize(out var errors))
        {
            throw new InvalidOperationException($"the signed proposal could not be finalized: {string.Join("; ", errors.Select(e => e.ToString()))}");
        }

        var transaction = signedPsbt.ExtractTransaction();
        var explorerClient = _explorerClientProvider.GetExplorerClient(network);
        var result = await explorerClient.BroadcastAsync(transaction).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException($"broadcast rejected: {result.RPCCodeMessage ?? result.RPCMessage ?? "unknown error"}");
        }

        var transactionId = transaction.GetHash().ToString();
        LogProposalBroadcast(_logger, session.SenderSessionId, transactionId, null);
        _senderSessionStore.CompleteSession(
            session.SenderSessionId,
            PayjoinSenderSessionStatus.CompletedPayjoin,
            transactionId,
            failureMessage: null);
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
