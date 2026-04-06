using NBitcoin;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;

internal sealed record BitcoindNodeOptions
{
    public string DockerExecutable { get; init; } = "docker";
    public string DockerImage { get; init; } = "btcpayserver/bitcoin:29.1";
    public string ContainerNamePrefix { get; init; } = "payjoin-cli-bitcoind";
    public string PublishedRpcHost { get; init; } = "127.0.0.1";
    public int ContainerRpcPort { get; init; } = 43782;
    public int FundingUtxoCount { get; init; } = 2;
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan SyncTimeout { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public Money FundingAmount { get; init; } = Money.Coins(0.15m);
    public string? DockerNetwork { get; init; }
    public string PrimaryNodeEndpoint { get; init; } = "host.docker.internal:39388";
}
