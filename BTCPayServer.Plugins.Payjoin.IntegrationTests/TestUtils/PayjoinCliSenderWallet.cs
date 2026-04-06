using BTCPayServer.Tests;
using NBitcoin.RPC;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;

internal sealed class PayjoinCliSenderWallet : IDisposable
{
    private readonly BitcoindNode _bitcoindNode;

    private PayjoinCliSenderWallet(
        BitcoindNode bitcoindNode,
        string walletName,
        Uri rpcHost,
        string rpcUser,
        string rpcPassword,
        RPCClient walletRpcClient)
    {
        _bitcoindNode = bitcoindNode;
        WalletName = walletName;
        RpcHost = rpcHost;
        RpcUser = rpcUser;
        RpcPassword = rpcPassword;
        WalletRpcClient = walletRpcClient;
    }

    public string WalletName { get; }
    public Uri RpcHost { get; }
    public string RpcUser { get; }
    public string RpcPassword { get; }
    public RPCClient WalletRpcClient { get; }

    public static async Task<PayjoinCliSenderWallet> CreateInitializedAsync(ServerTester tester, BTCPayNetwork network, CancellationToken cancellationToken)
    {
        return await CreateInitializedAsync(tester, network, options: null, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public static async Task<PayjoinCliSenderWallet> CreateInitializedAsync(ServerTester tester, BTCPayNetwork network, BitcoindNodeOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tester);
        ArgumentNullException.ThrowIfNull(network);

        options ??= new BitcoindNodeOptions();

        var bitcoindNode = await BitcoindNode.StartAsync(tester, network, options, cancellationToken).ConfigureAwait(false);
        try
        {
            var walletName = $"payjoin-cli-sender-{Guid.NewGuid():N}";
            var walletRpcClient = await bitcoindNode.CreateWalletAsync(walletName, cancellationToken).ConfigureAwait(false);
            await BitcoindWalletHelper.FundWalletAsync(tester.ExplorerNode, walletRpcClient, walletName, options, cancellationToken).ConfigureAwait(false);
            await bitcoindNode.WaitForSyncAsync(tester.ExplorerNode, cancellationToken).ConfigureAwait(false);

            return new PayjoinCliSenderWallet(
                bitcoindNode,
                walletName,
                bitcoindNode.BuildWalletRpcUri(walletName),
                bitcoindNode.RpcUser,
                bitcoindNode.RpcPassword,
                walletRpcClient);
        }
        catch
        {
            bitcoindNode.Dispose();
            throw;
        }
    }

    public async Task MineBlockAsync(CancellationToken cancellationToken)
    {
        var miningAddress = await WalletRpcClient.GetNewAddressAsync(cancellationToken).ConfigureAwait(false);
        await _bitcoindNode.MineBlockAsync(miningAddress, cancellationToken).ConfigureAwait(false);
    }

    public Task WaitForPrimaryNodeSyncAsync(ServerTester tester, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tester);
        return _bitcoindNode.WaitForSyncAsync(tester.ExplorerNode, cancellationToken);
    }

    public void Dispose()
    {
        _bitcoindNode.Dispose();
    }
}
