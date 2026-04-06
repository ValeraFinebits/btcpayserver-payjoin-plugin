using BTCPayServer.Tests;
using NBitcoin;
using NBitcoin.RPC;
using Newtonsoft.Json.Linq;
using System.Globalization;
using System.Net;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;

internal sealed class BitcoindNode : IDisposable
{
    private readonly BitcoindContainer _container;
    private readonly BitcoindNodeOptions _options;

    private BitcoindNode(BitcoindContainer container, BitcoindNodeOptions options, RPCClient nodeRpcClient)
    {
        _container = container;
        _options = options;
        NodeRpcClient = nodeRpcClient;
    }

    public Uri RpcHost => _container.RpcHost;
    public string RpcUser => _container.RpcUser;
    public string RpcPassword => _container.RpcPassword;
    public RPCClient NodeRpcClient { get; }

    public static Task<BitcoindNode> StartAsync(ServerTester tester, BTCPayNetwork network, CancellationToken cancellationToken)
    {
        return StartAsync(tester, network, options: null, cancellationToken: cancellationToken);
    }

    public static async Task<BitcoindNode> StartAsync(ServerTester tester, BTCPayNetwork network, BitcoindNodeOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tester);
        ArgumentNullException.ThrowIfNull(network);

        options ??= new BitcoindNodeOptions();
        var dockerClient = new DockerRunner(options.DockerExecutable);
        var container = await BitcoindContainer.StartAsync(dockerClient, options, cancellationToken).ConfigureAwait(false);
        try
        {
            var nodeRpcClient = new RPCClient($"{container.RpcUser}:{container.RpcPassword}", container.RpcHost, network.NBitcoinNetwork);
            var node = new BitcoindNode(container, options, nodeRpcClient);
            await node.InitializeAsync(tester.ExplorerNode, cancellationToken).ConfigureAwait(false);
            return node;
        }
        catch
        {
            container.Dispose();
            throw;
        }
    }

    public RPCClient CreateWalletRpcClient(string walletName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(walletName);
        return new RPCClient($"{RpcUser}:{RpcPassword}", BuildWalletRpcUri(walletName), NodeRpcClient.Network);
    }

    public async Task<RPCClient> CreateWalletAsync(string walletName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(walletName);
        await NodeRpcClient.SendCommandAsync("createwallet", walletName).WaitAsync(cancellationToken).ConfigureAwait(false);
        return CreateWalletRpcClient(walletName);
    }

    public async Task MineBlockAsync(BitcoinAddress miningAddress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(miningAddress);
        await NodeRpcClient.GenerateToAddressAsync(1, miningAddress, cancellationToken).ConfigureAwait(false);
    }

    public async Task WaitForSyncAsync(RPCClient primaryNodeRpcClient, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(primaryNodeRpcClient);

        BlockchainSnapshot? lastPrimarySnapshot = null;
        BlockchainSnapshot? lastNodeSnapshot = null;
        int? lastNodePeerCount = null;
        await AsyncPolling.WaitUntilAsync(
            _options.SyncTimeout,
            _options.PollInterval,
            async ct =>
            {
                lastPrimarySnapshot = await GetBlockchainSnapshotAsync(primaryNodeRpcClient, ct).ConfigureAwait(false);
                lastNodeSnapshot = await GetBlockchainSnapshotAsync(NodeRpcClient, ct).ConfigureAwait(false);
                lastNodePeerCount = await GetConnectionCountAsync(NodeRpcClient, ct).ConfigureAwait(false);

                return lastNodePeerCount > 0
                    && !lastNodeSnapshot.InitialBlockDownload
                    && lastNodeSnapshot.Blocks == lastNodeSnapshot.Headers
                    && lastNodeSnapshot.BestBlockHash == lastPrimarySnapshot.BestBlockHash;
            },
            IsTransientRpcException,
            lastException =>
                $"The dedicated payjoin-cli bitcoind node did not synchronize with the primary BTCPay test node within {_options.SyncTimeout.TotalSeconds:0} seconds. PrimaryBestBlock='{lastPrimarySnapshot?.BestBlockHash}', PrimaryBlocks={lastPrimarySnapshot?.Blocks}, DedicatedBestBlock='{lastNodeSnapshot?.BestBlockHash}', DedicatedBlocks={lastNodeSnapshot?.Blocks}, DedicatedHeaders={lastNodeSnapshot?.Headers}, DedicatedIbd={lastNodeSnapshot?.InitialBlockDownload}, DedicatedPeers={lastNodePeerCount?.ToString(CultureInfo.InvariantCulture) ?? "<unknown>"}, LastTransientError='{DescribeException(lastException)}'.",
            cancellationToken).ConfigureAwait(false);
    }

    public Uri BuildWalletRpcUri(string walletName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(walletName);
        var builder = new UriBuilder(RpcHost)
        {
            Path = $"/wallet/{Uri.EscapeDataString(walletName)}"
        };
        return builder.Uri;
    }

    public void Dispose()
    {
        _container.Dispose();
    }

    private async Task InitializeAsync(RPCClient primaryNodeRpcClient, CancellationToken cancellationToken)
    {
        try
        {
            await WaitForRpcAsync(cancellationToken).ConfigureAwait(false);
            await WaitForSyncAsync(primaryNodeRpcClient, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw await CreateInitializationExceptionAsync(ex).ConfigureAwait(false);
        }
    }

    private static async Task<BlockchainSnapshot> GetBlockchainSnapshotAsync(RPCClient rpcClient, CancellationToken cancellationToken)
    {
        var response = await rpcClient.SendCommandAsync("getblockchaininfo").WaitAsync(cancellationToken).ConfigureAwait(false);
        var result = response.Result as JObject ?? throw new InvalidOperationException("getblockchaininfo returned no object result.");

        return new BlockchainSnapshot(
            BestBlockHash: uint256.Parse(result.Value<string>("bestblockhash") ?? throw new InvalidOperationException("getblockchaininfo.bestblockhash was missing.")),
            Blocks: result.Value<int?>("blocks") ?? throw new InvalidOperationException("getblockchaininfo.blocks was missing."),
            Headers: result.Value<int?>("headers") ?? throw new InvalidOperationException("getblockchaininfo.headers was missing."),
            InitialBlockDownload: result.Value<bool?>("initialblockdownload") ?? throw new InvalidOperationException("getblockchaininfo.initialblockdownload was missing."));
    }

    private static async Task<int> GetConnectionCountAsync(RPCClient rpcClient, CancellationToken cancellationToken)
    {
        var response = await rpcClient.SendCommandAsync("getconnectioncount").WaitAsync(cancellationToken).ConfigureAwait(false);
        return response.Result?.Value<int>() ?? throw new InvalidOperationException("getconnectioncount returned no result.");
    }

    private async Task WaitForRpcAsync(CancellationToken cancellationToken)
    {
        await AsyncPolling.WaitUntilAsync(
            _options.StartupTimeout,
            _options.PollInterval,
            async ct =>
            {
                await NodeRpcClient.SendCommandAsync("getblockchaininfo").WaitAsync(ct).ConfigureAwait(false);
                return true;
            },
            IsTransientRpcException,
            lastException =>
                $"The dedicated payjoin-cli bitcoind node RPC endpoint '{RpcHost}' was not ready within {_options.StartupTimeout.TotalSeconds:0} seconds. LastTransientError='{DescribeException(lastException)}'.",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<InvalidOperationException> CreateInitializationExceptionAsync(Exception ex)
    {
        var diagnostics = await _container.GetDiagnosticsAsync(CancellationToken.None).ConfigureAwait(false);
        return new InvalidOperationException($"{ex.Message} {diagnostics}", ex);
    }

    internal static bool IsTransientRpcException(Exception ex)
    {
        return ex is RPCException or WebException or HttpRequestException;
    }

    internal static string DescribeException(Exception? ex)
    {
        return ex is null ? "<none>" : DockerRunner.EscapeMultiline(ex.Message);
    }

    private sealed record BlockchainSnapshot(uint256 BestBlockHash, int Blocks, int Headers, bool InitialBlockDownload);
}
