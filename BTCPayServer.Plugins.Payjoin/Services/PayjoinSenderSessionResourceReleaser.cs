using BTCPayServer.HostedServices;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

/// <summary>
/// Releases everything a session holds outside its own row once the session ends: the signing
/// request it may still be parked on, and the coin reservation that kept its outpoints away
/// from ordinary sends. Both are core pending transactions, and this plugin cannot lean on core
/// to retire them: core's chain watcher and expiry sweep live on an event loop that is never
/// started, so a row left behind holds its outpoints and looks actionable for ever.
///
/// Every terminal transition calls this, and a sweep finishes the job for a run that crashed
/// between completing and releasing. The cancels are no-ops on rows that are already terminal,
/// so releasing after an operator's own broadcast or cancellation is safe, and the cleared
/// columns are what stop the sweep from releasing again.
/// </summary>
internal static class PayjoinSenderSessionResourceReleaser
{
    internal static async Task ReleaseAsync(
        PendingTransactionService pendingTransactionService,
        PayjoinSenderSessionStore senderSessionStore,
        PayjoinSenderSessionState session)
    {
        if (session.PendingTransactionId is null && session.CoinReservationTransactionId is null)
        {
            return;
        }

        if (session.PendingTransactionId is not null)
        {
            await pendingTransactionService.CancelPendingTransaction(
                new PendingTransactionService.PendingTransactionFullId(
                    PayjoinConstants.BitcoinCode,
                    session.StoreId,
                    session.PendingTransactionId)).ConfigureAwait(false);
        }

        if (session.CoinReservationTransactionId is not null)
        {
            await pendingTransactionService.CancelPendingTransaction(
                new PendingTransactionService.PendingTransactionFullId(
                    PayjoinConstants.BitcoinCode,
                    session.StoreId,
                    session.CoinReservationTransactionId)).ConfigureAwait(false);
        }

        senderSessionStore.ClearReleasedResources(session.SenderSessionId);
    }
}
