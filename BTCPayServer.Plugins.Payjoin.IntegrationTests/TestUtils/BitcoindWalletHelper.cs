using NBitcoin.RPC;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;

internal static class BitcoindWalletHelper
{
    public static Task FundWalletAsync(RPCClient sourceRpcClient, RPCClient walletRpcClient, string walletName, CancellationToken cancellationToken)
    {
        return FundWalletAsync(sourceRpcClient, walletRpcClient, walletName, options: null, cancellationToken: cancellationToken);
    }

    public static async Task FundWalletAsync(RPCClient sourceRpcClient, RPCClient walletRpcClient, string walletName, BitcoindNodeOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceRpcClient);
        ArgumentNullException.ThrowIfNull(walletRpcClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(walletName);

        options ??= new BitcoindNodeOptions();

        for (var i = 0; i < options.FundingUtxoCount; i++)
        {
            var fundingAddress = await walletRpcClient.GetNewAddressAsync(cancellationToken).ConfigureAwait(false);
            await sourceRpcClient.SendToAddressAsync(fundingAddress, options.FundingAmount, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        var miningAddress = await sourceRpcClient.GetNewAddressAsync(cancellationToken).ConfigureAwait(false);
        await sourceRpcClient.GenerateToAddressAsync(1, miningAddress, cancellationToken).ConfigureAwait(false);
        await EnsureSpendableFundsAsync(walletRpcClient, walletName, options, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureSpendableFundsAsync(RPCClient walletRpcClient, string walletName, BitcoindNodeOptions options, CancellationToken cancellationToken)
    {
        await AsyncPolling.WaitUntilAsync(
            options.SyncTimeout,
            options.PollInterval,
            async ct =>
            {
                var unspent = await walletRpcClient.ListUnspentAsync().WaitAsync(ct).ConfigureAwait(false);
                return unspent.Length >= options.FundingUtxoCount;
            },
            BitcoindNode.IsTransientRpcException,
            lastException =>
                $"Dedicated payjoin-cli wallet '{walletName}' did not receive enough spendable funds within {options.SyncTimeout.TotalSeconds:0} seconds. LastTransientError='{BitcoindNode.DescribeException(lastException)}'.",
            cancellationToken).ConfigureAwait(false);
    }
}
