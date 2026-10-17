using System;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.RPC;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal enum OriginalTransactionVerdict { Accepted, Rejected, AlreadyKnown, Unavailable }

internal sealed record OriginalTransactionValidation(OriginalTransactionVerdict Verdict, string Reason);

internal interface IPayjoinOriginalTransactionValidator
{
    Task<OriginalTransactionValidation> ValidateAsync(byte[] original, CancellationToken cancellationToken);
}

internal sealed class PayjoinOriginalTransactionValidator(ExplorerClientProvider clients) : IPayjoinOriginalTransactionValidator
{
    public async Task<OriginalTransactionValidation> ValidateAsync(byte[] original, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var client = clients.GetExplorerClient(PayjoinConstants.BitcoinCode);
            if (client is null) return new(OriginalTransactionVerdict.Unavailable, "Bitcoin explorer is unavailable");
            var transaction = Transaction.Load(original, Network.Main);
            var result = await client.BroadcastAsync(transaction, testMempoolAccept: true, cancellationToken).ConfigureAwait(false);
            if (result is null) return new(OriginalTransactionVerdict.Unavailable, "Missing mempool acceptance response");
            if (result.Success) return new(OriginalTransactionVerdict.Accepted, "Original is mempool eligible");
            var reason = string.IsNullOrWhiteSpace(result.RPCMessage) ? result.RPCCodeMessage : result.RPCMessage;
            if (!result.RPCCode.HasValue || string.IsNullOrWhiteSpace(reason) || reason.StartsWith("RPC_", StringComparison.Ordinal))
                return new(OriginalTransactionVerdict.Unavailable, "Incomplete mempool acceptance response");
            if (result.RPCCode is not (RPCErrorCode.RPC_VERIFY_REJECTED or RPCErrorCode.RPC_VERIFY_ERROR or RPCErrorCode.RPC_VERIFY_ALREADY_IN_CHAIN))
                return new(OriginalTransactionVerdict.Unavailable, reason);

            var hash = transaction.GetHash();
            var known = await client.GetTransactionAsync(hash, cancellationToken).ConfigureAwait(false);
            if (known?.Transaction is { } knownTransaction && knownTransaction.GetHash() == hash &&
                known.TransactionHash == hash && knownTransaction.GetWitHash() == transaction.GetWitHash() &&
                known.Confirmations >= 0 && known.ReplacedBy is null)
                return new(OriginalTransactionVerdict.AlreadyKnown, "Original is already in the mempool or a block");

            var alreadyKnownReason = reason is "txn-already-in-mempool" or "txn-already-known" ||
                reason.Contains("already in block chain", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("already in blockchain", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("already in chain", StringComparison.OrdinalIgnoreCase);
            return !alreadyKnownReason && result.RPCCode is RPCErrorCode.RPC_VERIFY_REJECTED or RPCErrorCode.RPC_VERIFY_ERROR
                ? new(OriginalTransactionVerdict.Rejected, reason)
                : new(OriginalTransactionVerdict.Unavailable, reason);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or NBXplorer.Models.NBXplorerException or
            RPCException or OperationCanceledException or TimeoutException or Newtonsoft.Json.JsonException or FormatException or
            InvalidOperationException or ArgumentException or System.IO.IOException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(OriginalTransactionVerdict.Unavailable, ex.Message);
        }
    }
}
