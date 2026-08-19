using BTCPayServer.HostedServices;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

/// <summary>
/// Releases a session's coin reservation once the session ends. The reservation is a core
/// pending transaction, and this plugin cannot lean on core to retire it: core's chain watcher
/// and expiry sweep live on an event loop that is never started, so a row this plugin leaves
/// behind holds its outpoints for ever. Every terminal transition therefore releases explicitly,
/// and a sweep finishes the job for a run that crashed between completing and releasing.
///
/// The cancel is a no-op when the row is already terminal, so releasing after an operator's own
/// broadcast or cancellation is safe, and the cleared column is what stops the sweep from
/// releasing again.
/// </summary>
internal static class PayjoinSenderCoinReservationReleaser
{
    internal static async Task ReleaseAsync(
        PendingTransactionService pendingTransactionService,
        PayjoinSenderSessionStore senderSessionStore,
        PayjoinSenderSessionState session)
    {
        if (session.CoinReservationTransactionId is null)
        {
            return;
        }

        await pendingTransactionService.CancelPendingTransaction(
            new PendingTransactionService.PendingTransactionFullId(
                PayjoinConstants.BitcoinCode,
                session.StoreId,
                session.CoinReservationTransactionId)).ConfigureAwait(false);
        senderSessionStore.ClearCoinReservation(session.SenderSessionId);
    }
}
