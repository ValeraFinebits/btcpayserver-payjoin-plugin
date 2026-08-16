using BTCPayServer.HostedServices;
using BTCPayServer.Data;
using Microsoft.Extensions.Hosting;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

/// <summary>
/// Bridges BTCPay's pending transactions to the payjoin sender. A wallet that cannot sign on the
/// server signs through BTCPay's own screens, which support the vault, hardware devices, a seed
/// and multisig collection. Core stops there: it collects signatures and does not broadcast. This
/// listener picks the signed transaction up and hands it to the signature handler.
///
/// The event travels in memory, so it can be lost, and a cancelled or expired transaction never
/// produces one. The poller sweeps for those cases. This listener exists only to act at once when
/// the event does arrive.
/// </summary>
internal sealed class PayjoinSenderSignatureListener : IHostedService, System.IDisposable
{
    private readonly EventAggregator _eventAggregator;
    private readonly PayjoinSenderSessionStore _senderSessionStore;
    private readonly PayjoinSenderSignatureHandler _signatureHandler;
    private IEventAggregatorSubscription? _subscription;

    internal PayjoinSenderSignatureListener(
        EventAggregator eventAggregator,
        PayjoinSenderSessionStore senderSessionStore,
        PayjoinSenderSignatureHandler signatureHandler)
    {
        _eventAggregator = eventAggregator;
        _senderSessionStore = senderSessionStore;
        _signatureHandler = signatureHandler;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _eventAggregator.SubscribeAsync<PendingTransactionService.PendingTransactionEvent>(
            OnPendingTransactionEventAsync);
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
        // wallet keeps the row Pending, and the next event carries the one that completes it.
        if (evt?.Data is null || evt.Data.State != PendingTransactionState.Signed)
        {
            return;
        }

        if (!_senderSessionStore.TryGetSessionByPendingTransactionId(evt.Data.Id, out var session) || session is null)
        {
            return;
        }

        await _signatureHandler.HandleSignedAsync(session, evt.Data, CancellationToken.None).ConfigureAwait(false);
    }
}
