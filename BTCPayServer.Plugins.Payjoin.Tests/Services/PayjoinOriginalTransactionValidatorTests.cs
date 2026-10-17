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
        Assert.Single(fixture.Handler.Requests);
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

    private sealed class ExplorerFixture : IDisposable
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

    private sealed class FixtureProvider(BTCPayNetworkProvider networks, ExplorerClient client) : ExplorerClientProvider(networks, null!)
    {
        public bool Missing { get; set; }
        public override ExplorerClient GetExplorerClient(string cryptoCode) => Missing ? null! : client;
    }

    private sealed class ExplorerHandler(string response) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public string? Failure { get; set; }
        public string? Known { get; set; }
        public CancellationTokenSource? Cancel { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            Cancel?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure == "timeout") throw new TaskCanceledException("test timeout");
            if (Failure == "rpc-timeout") throw new TimeoutException("test RPC timeout");
            if (Failure == "rpc") throw new HttpRequestException("test RPC failure");
            if (Failure == "http") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var body = request.Method == HttpMethod.Post ? response : Known;
            return Task.FromResult(new HttpResponseMessage(body is null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            { Content = new StringContent(body ?? "", System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
