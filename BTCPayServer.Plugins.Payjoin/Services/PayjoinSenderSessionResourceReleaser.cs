using BTCPayServer.HostedServices;
using BTCPayServer.Plugins.Payjoin.Data;
using System.Linq;
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
    internal static async Task CompleteAsync(
        PendingTransactionService pendingTransactionService,
        PayjoinSenderSessionStore senderSessionStore,
        PayjoinSenderSessionState session,
        PayjoinSenderSessionStatus status,
        string? broadcastTransactionId,
        string? failureMessage)
    {
        senderSessionStore.CompleteSession(session.SenderSessionId, status, broadcastTransactionId, failureMessage);
        await ReleaseAsync(pendingTransactionService, senderSessionStore, session).ConfigureAwait(false);
    }

    internal static async Task ReleaseAsync(
        PendingTransactionService pendingTransactionService,
        PayjoinSenderSessionStore senderSessionStore,
        PayjoinSenderSessionState session)
    {
        if (!senderSessionStore.TryGetSession(session.SenderSessionId, out var current) || current is null ||
            current.Status is PayjoinSenderSessionStatus.Pending or PayjoinSenderSessionStatus.AwaitingSignature)
        {
            return;
        }

        foreach (var id in new[] { current.PendingTransactionId, current.CoinReservationTransactionId }
                     .Where(id => id is not null).Distinct())
        {
            await pendingTransactionService.CancelPendingTransaction(
                new PendingTransactionService.PendingTransactionFullId(
                    PayjoinConstants.BitcoinCode,
                    current.StoreId,
                    id!)).ConfigureAwait(false);
        }

        senderSessionStore.ClearReleasedResources(current);
    }
}
