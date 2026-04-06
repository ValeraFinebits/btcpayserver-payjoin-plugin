using System.Globalization;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;

internal sealed class BitcoindContainer : IDisposable
{
    private readonly DockerRunner _dockerClient;
    private readonly BitcoindNodeOptions _options;
    private bool _disposed;

    private BitcoindContainer(
        DockerRunner dockerClient,
        BitcoindNodeOptions options,
        string containerName,
        Uri rpcHost,
        string rpcUser,
        string rpcPassword)
    {
        _dockerClient = dockerClient;
        _options = options;
        ContainerName = containerName;
        RpcHost = rpcHost;
        RpcUser = rpcUser;
        RpcPassword = rpcPassword;
    }

    public string ContainerName { get; }
    public Uri RpcHost { get; }
    public string RpcUser { get; }
    public string RpcPassword { get; }

    public static async Task<BitcoindContainer> StartAsync(DockerRunner dockerClient, BitcoindNodeOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dockerClient);
        ArgumentNullException.ThrowIfNull(options);

        var containerName = $"{options.ContainerNamePrefix}-{Guid.NewGuid():N}";
        var rpcUser = $"cliuser{Guid.NewGuid():N}";
        var rpcPassword = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

        ProcessExecutionResult startupResult;
        try
        {
            startupResult = await dockerClient.RunAsync(BuildDockerRunArguments(options, containerName, rpcUser, rpcPassword), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            dockerClient.TryRemoveContainerNoThrow(containerName);
            throw;
        }

        if (startupResult.ExitCode != 0)
        {
            var diagnostics = await dockerClient.GetContainerDiagnosticsAsync(containerName, CancellationToken.None).ConfigureAwait(false);
            dockerClient.TryRemoveContainerNoThrow(containerName);
            throw new InvalidOperationException($"Failed to start the dedicated payjoin-cli bitcoind container. {DockerRunner.FormatResult(startupResult)} {diagnostics}");
        }

        try
        {
            var rpcPort = await dockerClient.GetPublishedPortAsync(containerName, options.ContainerRpcPort, cancellationToken).ConfigureAwait(false);
            var rpcHost = new Uri($"http://{options.PublishedRpcHost}:{rpcPort}/", UriKind.Absolute);
            return new BitcoindContainer(dockerClient, options, containerName, rpcHost, rpcUser, rpcPassword);
        }
        catch (OperationCanceledException)
        {
            dockerClient.TryRemoveContainerNoThrow(containerName);
            throw;
        }
        catch (Exception ex)
        {
            var diagnostics = await dockerClient.GetContainerDiagnosticsAsync(containerName, CancellationToken.None).ConfigureAwait(false);
            dockerClient.TryRemoveContainerNoThrow(containerName);
            throw new InvalidOperationException($"Failed to initialize the dedicated payjoin-cli bitcoind container metadata. {diagnostics}", ex);
        }
    }

    public async Task<string> GetDiagnosticsAsync(CancellationToken cancellationToken)
    {
        var containerDiagnostics = await _dockerClient.GetContainerDiagnosticsAsync(ContainerName, cancellationToken).ConfigureAwait(false);
        return $"ContainerName='{ContainerName}', RpcHost='{RpcHost}', RpcUser='{RpcUser}', PrimaryNodeEndpoint='{_options.PrimaryNodeEndpoint}', {containerDiagnostics}";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _dockerClient.TryRemoveContainerNoThrow(ContainerName);
    }

    private static IReadOnlyList<string> BuildDockerRunArguments(BitcoindNodeOptions options, string containerName, string rpcUser, string rpcPassword)
    {
        var arguments = new List<string>
        {
            "run",
            "--rm",
            "--detach",
            "--name",
            containerName,
            "--publish",
            $"{options.PublishedRpcHost}::{options.ContainerRpcPort}",
            "--env",
            "BITCOIN_NETWORK=regtest",
            "--env",
            "BITCOIN_WALLETDIR=/data/wallets",
            "--env",
            $"BITCOIN_EXTRA_ARGS={BuildBitcoinExtraArgs(options, rpcUser, rpcPassword)}"
        };

        if (!string.IsNullOrWhiteSpace(options.DockerNetwork))
        {
            arguments.Add("--network");
            arguments.Add(options.DockerNetwork);
        }

        if (options.PrimaryNodeEndpoint.Contains("host.docker.internal", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add("--add-host");
            arguments.Add("host.docker.internal:host-gateway");
        }

        arguments.Add(options.DockerImage);
        return arguments;
    }

    private static string BuildBitcoinExtraArgs(BitcoindNodeOptions options, string rpcUser, string rpcPassword)
    {
        return string.Join('\n', new[]
        {
            $"rpcuser={rpcUser}",
            $"rpcpassword={rpcPassword}",
            $"rpcport={options.ContainerRpcPort}",
            $"rpcbind=0.0.0.0:{options.ContainerRpcPort}",
            "rpcallowip=0.0.0.0/0",
            "listen=0",
            "deprecatedrpc=signrawtransaction",
            "fallbackfee=0.0002",
            "minrelaytxfee=0.00001000",
            $"connect={options.PrimaryNodeEndpoint}"
        });
    }
}
