using NBitcoin;
using NBitcoin.RPC;
using NBXplorer;
using System;
using System.Linq;
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
            throw new PayjoinSenderBroadcastException(
                $"broadcast rejected: {Reason(result) ?? "unknown error"}",
                IsPermanentRejection(result));
        }

        return transaction.GetHash().ToString();
    }

    /// <summary>
    /// The node's own words about a rejection. RPCMessage carries the specific reason (for
    /// example "txn-mempool-conflict"); RPCCodeMessage is only the generic text for the error
    /// code ("Transaction was rejected by network rules"), so both are needed and the specific
    /// one comes first.
    /// </summary>
    private static string? Reason(NBXplorer.Models.BroadcastResult result)
    {
        var parts = new[] { result.RPCMessage, result.RPCCodeMessage }
            .Where(part => !string.IsNullOrEmpty(part))
            .ToArray();
        return parts.Length == 0 ? null : string.Join("; ", parts);
    }

    private static bool IsPermanentRejection(NBXplorer.Models.BroadcastResult result)
    {
        // A transaction whose inputs are gone, or that conflicts with one the mempool already
        // holds, will never be accepted by retrying; everything else may be (fees, mempool
        // limits, node hiccups), so transient is the safe default. A conflict comes in two
        // shapes: "txn-mempool-conflict" when replacement is off, and a rejected replacement
        // ("insufficient fee, rejecting replacement ...") when both transactions signal RBF.
        // Both are final here, because the transactions this plugin holds are fully signed and
        // their fees can never be raised.
        var reason = Reason(result)?.Replace('-', ' ');
        return reason is not null &&
               (reason.Contains("missingorspent", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("missing inputs", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("mempool conflict", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("replacement", StringComparison.OrdinalIgnoreCase));
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

        // Core's reject reasons come in both spellings across versions: "txn-already-in-mempool"
        // and "Transaction already in block chain". Fold the hyphens away and match once.
        var reason = Reason(result)?.Replace('-', ' ');
        return reason is not null &&
               (reason.Contains("already known", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("already in block chain", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("already in mempool", StringComparison.OrdinalIgnoreCase));
    }
}
