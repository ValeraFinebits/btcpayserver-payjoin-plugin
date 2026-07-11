using BTCPayServer.Services.Wallets;
using NBitcoin;
using NBXplorer.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

// Narrows the platform wallet lookup (whose methods are not overridable) so accounting
// reconciliation can be exercised end to end in tests against a scripted chain view.
internal interface IPayjoinWalletTransactionReader
{
    Task<TransactionResult?> GetTransactionAsync(BTCPayNetwork network, uint256 transactionId, CancellationToken cancellationToken);
}

internal sealed class PayjoinWalletTransactionReader : IPayjoinWalletTransactionReader
{
    private readonly BTCPayWalletProvider _walletProvider;

    public PayjoinWalletTransactionReader(BTCPayWalletProvider walletProvider)
    {
        _walletProvider = walletProvider;
    }

    public async Task<TransactionResult?> GetTransactionAsync(BTCPayNetwork network, uint256 transactionId, CancellationToken cancellationToken)
    {
        var wallet = _walletProvider.GetWallet(network)
            ?? throw new InvalidOperationException($"Wallet for {network.CryptoCode} is not available.");
        return await wallet.GetTransactionAsync(transactionId, true, cancellationToken).ConfigureAwait(false);
    }
}
