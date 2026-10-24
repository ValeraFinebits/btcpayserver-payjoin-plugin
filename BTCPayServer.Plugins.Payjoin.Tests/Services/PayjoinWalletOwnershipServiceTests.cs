using BTCPayServer.Plugins.Payjoin.Services;
using NBitcoin;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinWalletOwnershipServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactPrevoutScriptIdentifiesOwnedAndForeignInputsWithoutDerivationHistory(bool coldWallet)
    {
        using var fixture = new OwnershipFixture(coldWallet);
        var owned = new OutPoint(fixture.TransactionId, 1);
        var foreign = new OutPoint(fixture.TransactionId, 0);
        var unavailable = new OutPoint(fixture.TransactionId, uint.MaxValue);

        var resolver = await fixture.ResolveAsync([owned, foreign, unavailable, owned], TestContext.Current.CancellationToken);

        Assert.Equal(coldWallet ? PayjoinInputOwnership.Foreign : PayjoinInputOwnership.Owned, resolver.GetOwnership(owned));
        Assert.Equal(PayjoinInputOwnership.Foreign, resolver.GetOwnership(foreign));
        Assert.Equal(PayjoinInputOwnership.Unknown, resolver.GetOwnership(unavailable));
        Assert.Equal(!coldWallet, resolver.IsOwned(owned.Hash.ToString(), owned.N));
        Assert.False(resolver.IsOwned(foreign.Hash.ToString(), foreign.N));
        Assert.Equal(1, fixture.FundingLookups);
        Assert.Equal(2, fixture.ScriptLookups);
    }

    [Theory]
    [InlineData("missing-funding")]
    [InlineData("missing-transaction")]
    [InlineData("wrong-transaction-id")]
    [InlineData("wrong-transaction-hash")]
    [InlineData("invalid-output-index")]
    public async Task IncompleteOrMismatchedFundingEvidenceRemainsUnknown(string evidence)
    {
        using var fixture = new OwnershipFixture();
        var input = new OutPoint(fixture.TransactionId, evidence == "invalid-output-index" ? uint.MaxValue : 1);
        switch (evidence)
        {
            case "missing-funding":
                fixture.Funding = null;
                break;
            case "missing-transaction":
                fixture.Funding!.Transaction = null;
                break;
            case "wrong-transaction-id":
                fixture.Funding!.TransactionHash = uint256.One;
                break;
            case "wrong-transaction-hash":
                fixture.Funding!.Transaction.LockTime = new LockTime(1);
                break;
        }

        var resolver = await fixture.ResolveAsync([input], TestContext.Current.CancellationToken);

        Assert.Equal(PayjoinInputOwnership.Unknown, resolver.GetOwnership(input));
        Assert.Throws<InvalidOperationException>(() => resolver.IsOwned(input.Hash.ToString(), input.N));
        Assert.Equal(0, fixture.ScriptLookups);
    }

    [Fact]
    public async Task RawTransactionAuthenticatesFundingWhenExplorerOmitsMetadataTransactionId()
    {
        using var fixture = new OwnershipFixture();
        fixture.Funding!.TransactionHash = null;
        var owned = new OutPoint(fixture.TransactionId, 1);

        var resolver = await fixture.ResolveAsync([owned], TestContext.Current.CancellationToken);

        Assert.Equal(PayjoinInputOwnership.Owned, resolver.GetOwnership(owned));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UnspentOutputsAuthenticatePrevoutsWithoutRawFunding(bool coldWallet, bool unconfirmed)
    {
        using var fixture = new OwnershipFixture(coldWallet);
        var transaction = fixture.Funding!.Transaction;
        var owned = new OutPoint(fixture.TransactionId, 1);
        var foreign = new OutPoint(fixture.TransactionId, 0);
        fixture.UnspentOutputs[(owned, unconfirmed)] = transaction.Outputs[1];
        fixture.UnspentOutputs[(foreign, unconfirmed)] = transaction.Outputs[0];
        fixture.Funding = null;

        var resolver = await fixture.ResolveAsync([owned, foreign], TestContext.Current.CancellationToken);

        Assert.Equal(coldWallet ? PayjoinInputOwnership.Foreign : PayjoinInputOwnership.Owned, resolver.GetOwnership(owned));
        Assert.Equal(PayjoinInputOwnership.Foreign, resolver.GetOwnership(foreign));
        Assert.Equal(1, fixture.FundingLookups);
        Assert.Equal(unconfirmed ? [false, true, false, true] : [false, false],
            fixture.RpcRequests.Select(request => request.IncludeMempool).ToArray());
    }

    [Fact]
    public async Task UnspentOutputAuthenticatesFundingWhenRawTransactionIsMissing()
    {
        using var fixture = new OwnershipFixture();
        var input = new OutPoint(fixture.TransactionId, 1);
        fixture.UnspentOutputs[(input, false)] = fixture.Funding!.Transaction.Outputs[1];
        fixture.Funding.Transaction = null;

        var resolver = await fixture.ResolveAsync([input], TestContext.Current.CancellationToken);

        Assert.Equal(PayjoinInputOwnership.Owned, resolver.GetOwnership(input));
        Assert.Single(fixture.RpcRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContradictoryFundingEvidenceDoesNotUseUnspentOutputFallback(bool missingRaw)
    {
        using var fixture = new OwnershipFixture();
        var input = new OutPoint(fixture.TransactionId, 1);
        fixture.UnspentOutputs[(input, false)] = fixture.Funding!.Transaction.Outputs[1];
        fixture.Funding.TransactionHash = uint256.One;
        if (missingRaw)
        {
            fixture.Funding.Transaction = null;
        }

        var resolver = await fixture.ResolveAsync([input], TestContext.Current.CancellationToken);

        Assert.Equal(PayjoinInputOwnership.Unknown, resolver.GetOwnership(input));
        Assert.Empty(fixture.RpcRequests);
        Assert.Equal(0, fixture.ScriptLookups);
    }

    [Fact]
    public async Task OutOfRangeOutputIndexDoesNotReachCoreRpc()
    {
        using var fixture = new OwnershipFixture();
        var input = new OutPoint(fixture.TransactionId, uint.MaxValue);
        fixture.Funding = null;

        var resolver = await fixture.ResolveAsync([input], TestContext.Current.CancellationToken);

        Assert.Equal(PayjoinInputOwnership.Unknown, resolver.GetOwnership(input));
        Assert.Empty(fixture.RpcRequests);
    }

    [Fact]
    public async Task RepeatedScriptsShareLookupOnlyWithinOneAttempt()
    {
        using var fixture = new OwnershipFixture();
        var owned = new OutPoint(fixture.TransactionId, 1);
        var repeated = new OutPoint(fixture.TransactionId, 2);

        var resolver = await fixture.ResolveAsync([owned, repeated, owned], TestContext.Current.CancellationToken);
        Assert.Equal(PayjoinInputOwnership.Owned, resolver.GetOwnership(owned));
        Assert.Equal(PayjoinInputOwnership.Owned, resolver.GetOwnership(repeated));
        Assert.Equal(1, fixture.FundingLookups);
        Assert.Equal(1, fixture.ScriptLookups);

        await fixture.ResolveAsync([owned, repeated], TestContext.Current.CancellationToken);
        Assert.Equal(2, fixture.FundingLookups);
        Assert.Equal(2, fixture.ScriptLookups);
        Assert.Empty(fixture.RpcRequests);
    }

    [Fact]
    public async Task RpcFailureDoesNotProduceAnOwnershipAnswer()
    {
        using var fixture = new OwnershipFixture();
        fixture.Funding = null;
        fixture.RpcFailure = new HttpRequestException("injected Core RPC failure");

        var input = new OutPoint(fixture.TransactionId, 0);
        var resolver = await fixture.ResolveAsync([input], TestContext.Current.CancellationToken);
        Assert.Equal(PayjoinInputOwnership.Unknown, resolver.GetOwnership(input));

        Assert.Equal(0, fixture.ScriptLookups);
    }

    [Fact]
    public async Task CancellationStopsBeforeWalletLookups()
    {
        using var fixture = new OwnershipFixture();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.ResolveAsync([new OutPoint(fixture.TransactionId, 0)], cancellation.Token));

        Assert.Equal(0, fixture.FundingLookups);
        Assert.Empty(fixture.RpcRequests);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("wrong-script")]
    [InlineData("wrong-path")]
    [InlineData("missing-path")]
    [InlineData("hardened-path")]
    [InlineData("null-record")]
    public async Task InvalidScriptEvidenceRemainsUnknown(string evidence)
    {
        using var fixture = new OwnershipFixture();
        fixture.Records = records =>
        {
            if (evidence == "null") return null;
            if (evidence == "null-record") return [null!];
            var record = Assert.Single(records);
            if (evidence == "wrong-script") record.ScriptPubKey = Script.Empty;
            if (evidence == "wrong-path") record.KeyPath = new KeyPath("0/1");
            if (evidence == "missing-path") record.KeyPath = null;
            if (evidence == "hardened-path") record.KeyPath = new KeyPath("0/0'");
            return records;
        };
        var input = new OutPoint(fixture.TransactionId, 1);
        var resolver = await fixture.ResolveAsync([input], TestContext.Current.CancellationToken);
        Assert.Equal(PayjoinInputOwnership.Unknown, resolver.GetOwnership(input));
    }

    [Theory]
    [InlineData("http")]
    [InlineData("timeout")]
    [InlineData("malformed")]
    public async Task FailedScriptLookupIsUnknownAndDeduplicatedPerPass(string error)
    {
        using var fixture = new OwnershipFixture();
        fixture.LookupFailure = error switch
        {
            "timeout" => new OperationCanceledException("dependency timeout"),
            "malformed" => new JsonSerializationException("invalid response"),
            _ => new HttpRequestException("dependency unavailable")
        };
        var input = new OutPoint(fixture.TransactionId, 1);
        var repeated = new OutPoint(fixture.TransactionId, 2);
        var resolver = await fixture.ResolveAsync([input, repeated], TestContext.Current.CancellationToken);
        Assert.Equal(PayjoinInputOwnership.Unknown, resolver.GetOwnership(input));
        Assert.Equal(PayjoinInputOwnership.Unknown, resolver.GetOwnership(repeated));
        Assert.Equal(1, fixture.ScriptLookups);
        fixture.LookupFailure = null;
        resolver = await fixture.ResolveAsync([input, repeated], TestContext.Current.CancellationToken);
        Assert.Equal(PayjoinInputOwnership.Owned, resolver.GetOwnership(input));
        Assert.Equal(PayjoinInputOwnership.Owned, resolver.GetOwnership(repeated));
        Assert.Equal(2, fixture.ScriptLookups);
    }

    [Fact]
    public async Task ProgrammingErrorIsNotSuppressed()
    {
        using var fixture = new OwnershipFixture { LookupFailure = new InvalidOperationException("programming error") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ResolveAsync(
            [new OutPoint(fixture.TransactionId, 1)], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CallerCancellationDuringLookupPropagates()
    {
        using var fixture = new OwnershipFixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        fixture.Records = _ =>
        {
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
            return [];
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.ResolveAsync(
            [new OutPoint(fixture.TransactionId, 1)], cancellation.Token));
    }

    [Fact]
    public async Task SuccessfulNullResponseRemainsUnknown()
    {
        using var fixture = new OwnershipFixture { SuccessfulNullResponse = true };
        var input = new OutPoint(fixture.TransactionId, 1);
        var resolver = await fixture.ResolveAsync([input], TestContext.Current.CancellationToken);
        Assert.Equal(PayjoinInputOwnership.Unknown, resolver.GetOwnership(input));
    }

    [Fact]
    public async Task StandaloneAddressDoesNotEstablishHotOwnership()
    {
        using var fixture = new OwnershipFixture();
        fixture.Records = records =>
        {
            var record = Assert.Single(records);
            record.DerivationStrategy = null;
            record.KeyPath = null;
            return records;
        };
        var input = new OutPoint(fixture.TransactionId, 1);
        var resolver = await fixture.ResolveAsync([input], TestContext.Current.CancellationToken);
        Assert.Equal(PayjoinInputOwnership.Foreign, resolver.GetOwnership(input));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnregisteredOrUnavailableWalletDoesNotAdvance(bool unavailable)
    {
        using var fixture = new OwnershipFixture();
        fixture.Tracked = unavailable;
        fixture.LookupFailure = unavailable ? new HttpRequestException("injected lookup failure") : null;
        var input = new OutPoint(fixture.TransactionId, 1);
        var resolver = await fixture.ResolveAsync([input], TestContext.Current.CancellationToken);
        Assert.Equal(PayjoinInputOwnership.Unknown, resolver.GetOwnership(input));
    }

    [Theory]
    [InlineData(false, "NBXplorerGenerated", Models.PayjoinAvailabilityStatus.MerchantRequirementsUnmet)]
    [InlineData(false, "ImportedSeed", Models.PayjoinAvailabilityStatus.MerchantRequirementsUnmet)]
    [InlineData(true, "SeedImported", Models.PayjoinAvailabilityStatus.MerchantRequirementsUnmet)]
    [InlineData(true, "Manual", Models.PayjoinAvailabilityStatus.MerchantRequirementsUnmet)]
    [InlineData(true, "NBXplorerGenerated", Models.PayjoinAvailabilityStatus.Active)]
    [InlineData(true, "ImportedSeed", Models.PayjoinAvailabilityStatus.Active)]
    public void BtcpayCreatedAndRestoredHotProfilesAreSupported(bool hot, string source, Models.PayjoinAvailabilityStatus expected)
    {
        var factory = new NBXplorerNetworkProvider(ChainName.Regtest).GetFromCryptoCode("BTC").DerivationStrategyFactory;
        var settings = new DerivationSchemeSettings
        {
            IsHotWallet = hot, Source = source,
            AccountDerivation = factory.CreateDirectDerivationStrategy(new ExtKey().Neuter(), new DerivationStrategyOptions())
        };
        Assert.Equal(expected, PayjoinWalletContext.GetProfileStatus(settings));
        Assert.Equal(Models.PayjoinAvailabilityStatus.TemporarilyUnavailable, PayjoinWalletContext.GetProfileStatus(null));
        settings.IsHotWallet = true;
        settings.Source = source is "NBXplorerGenerated" or "ImportedSeed" ? source : "NBXplorerGenerated";
        settings.AccountDerivation = null!;
        Assert.Equal(Models.PayjoinAvailabilityStatus.TemporarilyUnavailable, PayjoinWalletContext.GetProfileStatus(settings));
    }

    [Fact]
    public void OutputMatcherSelectsOnlyInvoiceWithoutWalletLookups()
    {
        using var invoice = new Key();
        using var hot = new Key();
        using var cold = new Key();
        byte[][] scripts = [invoice.PubKey.WitHash.ScriptPubKey.ToBytes(), hot.PubKey.WitHash.ScriptPubKey.ToBytes(), cold.PubKey.WitHash.ScriptPubKey.ToBytes()];
        var resolver = new PayjoinInvoiceOutputMatcher(scripts[0]);
        Assert.True(resolver.Matches(scripts[0]));
        Assert.False(resolver.Matches(scripts[1]));
        Assert.False(resolver.Matches(scripts[2]));
    }

    internal sealed class OwnershipFixture : IDisposable
    {
        private readonly Handler _handler;
        private readonly HttpClient _http;
        private readonly ExplorerClient _client;
        private readonly DerivationStrategyBase _hot;
        private readonly DerivationStrategyBase? _cold;
        private readonly DerivationStrategyBase _owner;
        private readonly Script _ownedScript;

        public uint256 TransactionId { get; }
        public TransactionResult? Funding { get; set; }
        public int FundingLookups { get; private set; }
        public int ScriptLookups { get; private set; }
        public Dictionary<(OutPoint Input, bool IncludeMempool), TxOut> UnspentOutputs { get; } = [];
        public List<(OutPoint Input, bool IncludeMempool)> RpcRequests { get; } = [];
        public Exception? RpcFailure { get; set; }
        public Exception? LookupFailure { get; set; }
        public bool Tracked { get; set; } = true;
        public bool SuccessfulNullResponse { get; set; }
        public Func<KeyPathInformation[], KeyPathInformation[]?>? Records { get; set; }

        public OwnershipFixture(bool coldWallet = false)
        {
            var network = new NBXplorerNetworkProvider(ChainName.Regtest).GetFromCryptoCode("BTC");
            _client = network.CreateExplorerClient(new Uri("http://ownership.example/"));
            _hot = network.DerivationStrategyFactory.CreateDirectDerivationStrategy(new ExtKey().Neuter(),
                new DerivationStrategyOptions { ScriptPubKeyType = ScriptPubKeyType.Segwit });
            _cold = coldWallet
                ? network.DerivationStrategyFactory.CreateDirectDerivationStrategy(new ExtKey().Neuter(),
                    new DerivationStrategyOptions { ScriptPubKeyType = ScriptPubKeyType.Segwit })
                : null;
            _owner = _cold ?? _hot;
            _ownedScript = ((StandardDerivationStrategyBase)_owner).GetDerivation(new KeyPath("0/0")).ScriptPubKey;
            using var foreignKey = new Key();
            var transaction = Network.RegTest.CreateTransaction();
            transaction.Inputs.Add(new OutPoint(uint256.One, 0));
            transaction.Outputs.Add(Money.Satoshis(1_000), foreignKey.PubKey.WitHash.ScriptPubKey);
            transaction.Outputs.Add(Money.Satoshis(2_000), _ownedScript);
            transaction.Outputs.Add(Money.Satoshis(3_000), _ownedScript);
            TransactionId = transaction.GetHash();
            Funding = new TransactionResult { TransactionHash = TransactionId, Transaction = transaction };
            _handler = new Handler(this);
            _http = new HttpClient(_handler, disposeHandler: false);
            _client.SetClient(_http);
            _client.SetNoAuth();
            _client.RPCClient.HttpClient = _http;
        }

        public Task<PayjoinInputOwnershipResolver> ResolveAsync(IReadOnlyCollection<OutPoint> inputs, CancellationToken cancellationToken) =>
            PayjoinWalletOwnershipService.CreateInputResolverAsync(_client, _hot, inputs, cancellationToken);

        private async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            object? response;
            if (path == $"/v1/cryptos/BTC/derivations/{_hot}")
            {
                return new HttpResponseMessage(Tracked ? HttpStatusCode.OK : HttpStatusCode.NotFound);
            }
            if (LookupFailure is not null && path.Contains("/scripts/", StringComparison.Ordinal))
            {
                ScriptLookups++;
                throw LookupFailure;
            }
            if (path == "/v1/cryptos/BTC/rpc")
            {
                if (RpcFailure is not null)
                {
                    throw RpcFailure;
                }

                var rpc = JObject.Parse(await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                Assert.Equal("gettxout", rpc["method"]!.ToString());
                var parameters = (JArray)rpc["params"]!;
                var input = new OutPoint(uint256.Parse(parameters[0]!.ToString()), (uint)parameters[1]!);
                var includeMempool = (bool)parameters[2]!;
                RpcRequests.Add((input, includeMempool));
                var output = UnspentOutputs.GetValueOrDefault((input, includeMempool));
                return JsonResponse(new
                {
                    result = output is null ? null : new
                    {
                        bestblock = uint256.One.ToString(),
                        confirmations = includeMempool ? 0 : 1,
                        value = output.Value.ToDecimal(MoneyUnit.BTC),
                        scriptPubKey = new { asm = output.ScriptPubKey.ToString(), hex = output.ScriptPubKey.ToHex(), type = "witness_v0_keyhash" },
                        coinbase = false
                    },
                    error = (object?)null,
                    id = rpc["id"]
                });
            }

            if (path.StartsWith("/v1/cryptos/BTC/transactions/", StringComparison.Ordinal))
            {
                FundingLookups++;
                response = path == $"/v1/cryptos/BTC/transactions/{TransactionId}" ? Funding : null;
            }
            else if (path.Contains("/scripts/", StringComparison.Ordinal))
            {
                ScriptLookups++;
                if (SuccessfulNullResponse) return JsonResponse(null);
                Assert.StartsWith("/v1/cryptos/BTC/scripts/", path, StringComparison.Ordinal);
                KeyPathInformation[] records = path == $"/v1/cryptos/BTC/scripts/{_ownedScript.ToHex()}"
                    ? [new KeyPathInformation { DerivationStrategy = _owner, ScriptPubKey = _ownedScript, KeyPath = new KeyPath("0/0") }]
                    : [];
                response = Records is null ? records : Records(records);
            }
            else
            {
                throw new InvalidOperationException("Ownership must not depend on derivation-scoped transaction history: " + path);
            }

            return response is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : JsonResponse(response);
        }

        private HttpResponseMessage JsonResponse(object? response) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonConvert.SerializeObject(response, _client.Network.Serializer.Settings),
                System.Text.Encoding.UTF8, "application/json")
        };

        public void Dispose()
        {
            _http.Dispose();
            _handler.Dispose();
        }

        private sealed class Handler(OwnershipFixture fixture) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                fixture.RespondAsync(request, cancellationToken);
        }
    }
}

