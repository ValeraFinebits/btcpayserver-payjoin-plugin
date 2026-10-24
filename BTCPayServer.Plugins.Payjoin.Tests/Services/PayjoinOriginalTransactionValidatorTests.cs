using System.Net;
using BTCPayServer.Logging;
using BTCPayServer.Plugins.Payjoin.Services;
using NBXplorer;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinOriginalTransactionValidatorTests
{
    [Theory]
    [InlineData("{\"success\":true}", "Accepted")]
    [InlineData("{}", "Unavailable")]
    [InlineData("{\"success\":false}", "Unavailable")]
    [InlineData("{\"success\":false,\"rpcCode\":-26}", "Unavailable")]
    [InlineData("{\"success\":false,\"rpcCode\":-25,\"rpcCodeMessage\":\"RPC_VERIFY_ERROR\",\"rpcMessage\":null}", "Unavailable")]
    [InlineData("{\"success\":false,\"rpcCode\":-25,\"rpcCodeMessage\":\"mempool-script-verify-flag-failed (Signature must be zero for failed CHECK(MULTI)SIG operation)\",\"rpcMessage\":null}", "Rejected")]
    [InlineData("{\"success\":false,\"rpcCode\":-25,\"rpcCodeMessage\":\"bad-txns-inputs-missingorspent\"}", "Rejected")]
    [InlineData("{\"success\":false,\"rpcCode\":-25,\"rpcCodeMessage\":\"txn-already-in-mempool\",\"rpcMessage\":null}", "Unavailable")]
    [InlineData("{\"success\":false,\"rpcCode\":-26,\"rpcCodeMessage\":\"RPC_VERIFY_REJECTED\",\"rpcMessage\":\"mandatory-script-verify-flag-failed\"}", "Rejected")]
    [InlineData("{\"success\":false,\"rpcCode\":-32603,\"rpcCodeMessage\":\"RPC_INTERNAL_ERROR\",\"rpcMessage\":\"node unavailable\"}", "Unavailable")]
    [InlineData("not JSON", "Unavailable")]
    public async Task RequiresAnExplicitMempoolVerdict(string response, string expected)
    {
        using var fixture = new ExplorerFixture(response);
        var actual = await fixture.Validator.ValidateAsync(fixture.Transaction.ToBytes(), TestContext.Current.CancellationToken);
        Assert.Equal(Enum.Parse<OriginalTransactionVerdict>(expected), actual.Verdict);
        Assert.Contains(fixture.Handler.Requests, request => request.Contains("testMempoolAccept=true", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(expected == "Rejected" ? 2 : 1, fixture.Handler.StatusReads);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("rpc")]
    [InlineData("timeout")]
    [InlineData("rpc-timeout")]
    [InlineData("missing-service")]
    public async Task ServiceFailureDoesNotProveOriginalInvalidity(string failure)
    {
        using var fixture = new ExplorerFixture("{}");
        fixture.Handler.Failure = failure;
        if (failure == "missing-service") fixture.Provider.Missing = true;
        var result = await fixture.Validator.ValidateAsync(fixture.Transaction.ToBytes(), TestContext.Current.CancellationToken);
        Assert.Equal(OriginalTransactionVerdict.Unavailable, result.Verdict);
        Assert.Equal(failure != "missing-service", fixture.Handler.Originals.Count == 1);
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using var fixture = new ExplorerFixture("{}");
        using var cancellation = new CancellationTokenSource();
        fixture.Handler.Cancel = cancellation;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Validator.ValidateAsync(fixture.Transaction.ToBytes(), cancellation.Token));
    }

    [Fact]
    public async Task RpcFailureRemainsUnavailableEvenIfAnExplorerHasCachedTransactionData()
    {
        using var fixture = new ExplorerFixture("{\"success\":false,\"rpcCode\":-32603,\"rpcCodeMessage\":\"RPC_INTERNAL_ERROR\",\"rpcMessage\":\"RPC service failed\"}");
        fixture.Handler.Known = $$"""{"transactionHash":"{{fixture.Transaction.GetHash()}}","transaction":"{{fixture.Transaction.ToHex()}}","confirmations":1}""";
        Assert.Equal(OriginalTransactionVerdict.Unavailable, (await fixture.Validator.ValidateAsync(fixture.Transaction.ToBytes(), TestContext.Current.CancellationToken)).Verdict);
        Assert.DoesNotContain(fixture.Handler.Requests, request => request.Contains("/transactions/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, 0, "AlreadyKnown")]
    [InlineData(false, 1, "AlreadyKnown")]
    [InlineData(true, 1, "Unavailable")]
    [InlineData(false, -1, "Unavailable")]
    public async Task AlreadyKnownRequiresTheExactObservableTransaction(bool otherTransaction, int confirmations, string expected)
    {
        using var fixture = new ExplorerFixture("{\"success\":false,\"rpcCode\":-25,\"rpcCodeMessage\":\"txn-already-in-mempool\",\"rpcMessage\":null}");
        var known = fixture.Transaction.Clone();
        if (otherTransaction) known.LockTime = 42;
        fixture.Handler.Known = $$"""{"transactionHash":"{{known.GetHash()}}","transaction":"{{known.ToHex()}}","confirmations":{{confirmations}}} """;
        Assert.Equal(Enum.Parse<OriginalTransactionVerdict>(expected), (await fixture.Validator.ValidateAsync(fixture.Transaction.ToBytes(), TestContext.Current.CancellationToken)).Verdict);
    }

    [Theory]
    [InlineData(false, "AlreadyKnown")]
    [InlineData(true, "Unavailable")]
    public async Task AlreadyKnownRequiresTheSameWitnessForTheSameTransactionId(bool differentWitness, string expected)
    {
        using var fixture = new ExplorerFixture("{\"success\":false,\"rpcCode\":-25,\"rpcCodeMessage\":\"txn-already-in-mempool\"}");
        var original = fixture.Transaction;
        original.Inputs[0].WitScript = new WitScript(new[] { new byte[] { 0x01 }, new byte[] { 0x02 } });
        var known = original.Clone();
        if (differentWitness) known.Inputs[0].WitScript = new WitScript(new[] { new byte[] { 0x03 }, new byte[] { 0x02 } });

        Assert.NotEmpty(original.Inputs[0].WitScript.Pushes);
        Assert.Equal(original.GetHash(), known.GetHash());
        if (differentWitness) Assert.NotEqual(original.GetWitHash(), known.GetWitHash());
        else Assert.Equal(original.GetWitHash(), known.GetWitHash());

        fixture.Handler.Known = $$"""{"transactionHash":"{{known.GetHash()}}","transaction":"{{known.ToHex()}}","confirmations":0}""";
        var result = await fixture.Validator.ValidateAsync(original.ToBytes(), TestContext.Current.CancellationToken);
        Assert.Equal(Enum.Parse<OriginalTransactionVerdict>(expected), result.Verdict);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("{\"isFullySynched\":false,\"bitcoinStatus\":{\"isSynched\":true,\"blocks\":100,\"headers\":100}}")]
    [InlineData("{\"isFullySynched\":true}")]
    [InlineData("{\"isFullySynched\":true,\"bitcoinStatus\":{\"isSynched\":false,\"blocks\":100,\"headers\":100}}")]
    [InlineData("{\"isFullySynched\":true,\"bitcoinStatus\":{\"isSynched\":true,\"blocks\":100,\"headers\":101}}")]
    public async Task UnreadyStatusNeverTestsTheOriginal(string? status)
    {
        using var fixture = new ExplorerFixture("{\"success\":true}");
        fixture.Handler.Statuses.Enqueue(status);
        Assert.Equal(OriginalTransactionVerdict.Unavailable, (await fixture.Validator.ValidateAsync(fixture.Transaction.ToBytes(), TestContext.Current.CancellationToken)).Verdict);
        Assert.Empty(fixture.Handler.Originals);
        Assert.Equal(1, fixture.Handler.StatusReads);
    }

    [Fact]
    public async Task ReadinessRecoveryAcceptsTheSameOriginalWithoutASecondStatusRead()
    {
        using var fixture = new ExplorerFixture("{\"success\":true}");
        var original = fixture.Transaction.ToBytes();
        fixture.Handler.Statuses.Enqueue("{}");
        Assert.Equal(OriginalTransactionVerdict.Unavailable, (await fixture.Validator.ValidateAsync(original, TestContext.Current.CancellationToken)).Verdict);
        Assert.Equal(OriginalTransactionVerdict.Accepted, (await fixture.Validator.ValidateAsync(original, TestContext.Current.CancellationToken)).Verdict);
        Assert.Equal(original, Assert.Single(fixture.Handler.Originals));
        Assert.Equal(2, fixture.Handler.StatusReads);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData(null)]
    [InlineData("{\"isFullySynched\":true,\"bitcoinStatus\":{\"isSynched\":true,\"blocks\":100,\"headers\":101}}")]
    public async Task LosingReadinessBeforeRejectionMakesTheVerdictUnavailable(string? secondStatus)
    {
        using var fixture = new ExplorerFixture("{\"success\":false,\"rpcCode\":-25,\"rpcCodeMessage\":\"missing-inputs\"}");
        fixture.Handler.Statuses.Enqueue(ExplorerHandler.Ready);
        fixture.Handler.Statuses.Enqueue(secondStatus);
        Assert.Equal(OriginalTransactionVerdict.Unavailable, (await fixture.Validator.ValidateAsync(fixture.Transaction.ToBytes(), TestContext.Current.CancellationToken)).Verdict);
        Assert.Single(fixture.Handler.Originals);
        Assert.Equal(2, fixture.Handler.StatusReads);
    }

    [Theory]
    [InlineData(1, "http")]
    [InlineData(1, "rpc")]
    [InlineData(1, "timeout")]
    [InlineData(1, "rpc-timeout")]
    [InlineData(1, "malformed")]
    [InlineData(2, "http")]
    [InlineData(2, "rpc")]
    [InlineData(2, "timeout")]
    [InlineData(2, "rpc-timeout")]
    [InlineData(2, "malformed")]
    public async Task StatusReadFailureCannotProveInvalidity(int read, string failure)
    {
        using var fixture = new ExplorerFixture("{\"success\":false,\"rpcCode\":-25,\"rpcCodeMessage\":\"missing-inputs\"}");
        fixture.Handler.FaultEndpoint = "status:" + read;
        fixture.Handler.Failure = failure;
        Assert.Equal(OriginalTransactionVerdict.Unavailable, (await fixture.Validator.ValidateAsync(fixture.Transaction.ToBytes(), TestContext.Current.CancellationToken)).Verdict);
        Assert.Equal(read, fixture.Handler.StatusReads);
        Assert.Equal(read - 1, fixture.Handler.Originals.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CallerCancellationOnEitherStatusReadPropagates(int read)
    {
        using var fixture = new ExplorerFixture("{\"success\":false,\"rpcCode\":-25,\"rpcCodeMessage\":\"missing-inputs\"}");
        using var cancellation = new CancellationTokenSource();
        fixture.Handler.Cancel = cancellation;
        fixture.Handler.FaultEndpoint = "status:" + read;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Validator.ValidateAsync(fixture.Transaction.ToBytes(), cancellation.Token));
        Assert.Equal(read, fixture.Handler.StatusReads);
    }

    internal sealed class ExplorerFixture : IDisposable
    {
        private readonly HttpClient _http;
        public ExplorerHandler Handler { get; }
        public FixtureProvider Provider { get; }
        public PayjoinOriginalTransactionValidator Validator { get; }
        public Transaction Transaction { get; } = Network.RegTest.CreateTransaction();
        public ExplorerFixture(string response)
        {
            Transaction.Inputs.Add(new OutPoint(uint256.One, 0));
            Transaction.Outputs.Add(Money.Satoshis(50_000), Script.FromHex("51"));
            Handler = new ExplorerHandler(response);
            _http = new HttpClient(Handler);
            var nbx = new NBXplorerNetworkProvider(ChainName.Regtest);
            var networks = new BTCPayNetworkProvider([new BTCPayNetwork { CryptoCode = "BTC", NBXplorerNetwork = nbx.GetFromCryptoCode("BTC") }], nbx, new Logs());
            var client = nbx.GetFromCryptoCode("BTC").CreateExplorerClient(new Uri("http://explorer/"));
            client.SetClient(_http);
            client.SetNoAuth();
            Provider = new FixtureProvider(networks, client);
            Validator = new PayjoinOriginalTransactionValidator(Provider);
        }
        public void Dispose() => _http.Dispose();
    }

    internal sealed class FixtureProvider(BTCPayNetworkProvider networks, ExplorerClient client) : ExplorerClientProvider(networks, null!)
    {
        public bool Missing { get; set; }
        public override ExplorerClient GetExplorerClient(string cryptoCode) => Missing ? null! : client;
    }

    internal sealed class ExplorerHandler(string response) : HttpMessageHandler
    {
        public const string Ready = "{\"isFullySynched\":true,\"bitcoinStatus\":{\"isSynched\":true,\"blocks\":100,\"headers\":100}}";
        public string Response { get; set; } = response;
        public List<string> Requests { get; } = [];
        public List<byte[]> Originals { get; } = [];
        public Queue<string?> Statuses { get; } = new();
        public int StatusReads { get; private set; }
        public string FaultEndpoint { get; set; } = "dry-run";
        public string? Failure { get; set; }
        public string? Known { get; set; }
        public CancellationTokenSource? Cancel { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            var status = request.RequestUri.AbsolutePath.EndsWith("/status", StringComparison.Ordinal);
            var dryRun = request.Method == HttpMethod.Post;
            var endpoint = status ? "status:" + ++StatusReads : dryRun ? "dry-run" : "lookup";
            if (dryRun) Originals.Add(await request.Content!.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
            if (endpoint == FaultEndpoint)
            {
                Cancel?.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
                if (Failure == "timeout") throw new TaskCanceledException("test timeout");
                if (Failure == "rpc-timeout") throw new TimeoutException("test RPC timeout");
                if (Failure == "rpc") throw new HttpRequestException("test RPC failure");
                if (Failure == "http") return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                if (Failure == "malformed") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not JSON") };
            }
            var body = status ? Statuses.Count > 0 ? Statuses.Dequeue() : Ready : dryRun ? Response : Known;
            return new HttpResponseMessage(body is null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            { Content = new StringContent(body ?? "", System.Text.Encoding.UTF8, "application/json") };
        }
    }
}
