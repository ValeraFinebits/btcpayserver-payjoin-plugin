using NBitcoin;
using NBitcoin.RPC;
using NBXplorer;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

/// <summary>
/// Broadcasts a sender transaction and treats a transaction the network already holds as a
/// success. Several routes reach the same broadcast: the poller, the signature listener, a
/// retry after a timeout, and the operator's own broadcast button on BTCPay's
/// pending-transaction screen. Only the first of them gets an accepting answer, and a payment
/// that reached the network must never be recorded as a failure.
/// </summary>
internal static class PayjoinSenderBroadcaster
{
    internal static async Task<string> BroadcastAsync(
        ExplorerClient explorerClient,
        Transaction transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(explorerClient);
        ArgumentNullException.ThrowIfNull(transaction);

        var result = await explorerClient.BroadcastAsync(transaction, cancellationToken).ConfigureAwait(false);
        if (!result.Success && !IsAlreadyBroadcast(result))
        {
            throw new InvalidOperationException(
                $"broadcast rejected: {result.RPCCodeMessage ?? result.RPCMessage ?? "unknown error"}");
        }

        return transaction.GetHash().ToString();
    }

    private static bool IsAlreadyBroadcast(NBXplorer.Models.BroadcastResult result)
    {
        // Bitcoin Core answers a transaction it already has in one of two ways: a dedicated code
        // when the transaction is mined, and the generic rejection code with a reason string when
        // it is still in the mempool.
        if (result.RPCCode == RPCErrorCode.RPC_VERIFY_ALREADY_IN_CHAIN)
        {
            return true;
        }

        var reason = result.RPCCodeMessage ?? result.RPCMessage;
        return reason is not null &&
               (reason.Contains("already-known", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("already in block chain", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("already in mempool", StringComparison.OrdinalIgnoreCase));
    }
}
