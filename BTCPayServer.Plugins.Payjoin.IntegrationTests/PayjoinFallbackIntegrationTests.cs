using BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;
using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services.Wallets;
using BTCPayServer.Tests;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer.Models;
using Newtonsoft.Json.Linq;
using Payjoin;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class PayjoinFallbackIntegrationTests(ITestOutputHelper output) : UnitTestBase(output)
{
    [Theory]
    [InlineData("success")]
    [InlineData("missing-inputs")]
    [InlineData("missing-parent")]
    [InlineData("non-final")]
    [Trait("Integration", "Integration")]
    public async Task PersistedFallbackRetriesTheSameOriginalAfterCoreConditionsRecover(string scenario)
    {
        using var timeout = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        var token = timeout.Token;
        using var tester = CreateServerTester(scope: $"cancelled-fallback-{scenario}", newDb: true);
        var account = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester,
            initialFundingUtxoCount: 1, cancellationToken: token);
        foreach (var poller in tester.PayTester.GetService<IEnumerable<IHostedService>>().OfType<PayjoinReceiverPoller>())
            await poller.StopAsync(token);
        var client = tester.PayTester.GetService<ExplorerClientProvider>().GetExplorerClient(account.Network);
        using var receiverKey = new Key();
        var address = receiverKey.PubKey.WitHash.GetAddress(Network.RegTest);
        PSBT originalPsbt;
        Transaction? parent = null;
        if (scenario == "missing-inputs")
        {
            using var senderKey = new Key();
            var coin = new Coin(new NBitcoin.OutPoint(uint256.One, 0),
                new NBitcoin.TxOut(Money.Coins(1m), senderKey.PubKey.WitHash));
            originalPsbt = Network.RegTest.CreateTransactionBuilder().AddCoins(coin).AddKeys(senderKey)
                .Send(address, Money.Satoshis(50_000)).SetChange(senderKey.PubKey.WitHash)
                .SendFees(Money.Satoshis(1000)).BuildPSBT(true).Finalize();
        }
        else if (scenario == "non-final")
        {
            var height = await tester.ExplorerNode.GetBlockCountAsync(token);
            var funded = await tester.ExplorerNode.SendCommandAsync("walletcreatefundedpsbt",
                new JArray(), new JObject { [address.ToString()] = 0.0005m },
                height + 1, new JObject { ["replaceable"] = true, ["fee_rate"] = 5 }).WaitAsync(token);
            var signed = await tester.ExplorerNode.SendCommandAsync("walletprocesspsbt", (string)funded.Result["psbt"]!).WaitAsync(token);
            // Core 32 defaults to PSBTv2; rust-payjoin expects PSBTv0.
            originalPsbt = PSBT.Parse((string)signed.Result["psbt"]!, Network.RegTest).ToPSBTv0().Finalize();
        }
        else if (scenario == "missing-parent")
        {
            using var childKey = new Key();
            var childAddress = childKey.PubKey.WitHash.GetAddress(Network.RegTest);
            var parentPsbt = (await client.CreatePSBTAsync(account.Merchant.DerivationScheme, new CreatePSBTRequest
            {
                FeePreference = new FeePreference { ExplicitFeeRate = new FeeRate(5m) },
                Destinations = { new CreatePSBTDestination { Destination = childAddress, Amount = Money.Coins(0.01m) } }
            }, token)).PSBT;
            parentPsbt = await account.Merchant.Sign(parentPsbt);
            parentPsbt.Finalize();
            parent = parentPsbt.ExtractTransaction();
            var index = Enumerable.Range(0, parent.Outputs.Count).Single(i => parent.Outputs[i].ScriptPubKey == childAddress.ScriptPubKey);
            var coin = new Coin(new NBitcoin.OutPoint(parent.GetHash(), (uint)index), parent.Outputs[index]);
            originalPsbt = Network.RegTest.CreateTransactionBuilder().AddCoins(coin).AddKeys(childKey)
                .Send(address, Money.Satoshis(50_000)).SetChange(childAddress)
                .SendFees(Money.Satoshis(1000)).BuildPSBT(true).Finalize();
        }
        else
        {
            originalPsbt = (await client.CreatePSBTAsync(account.Merchant.DerivationScheme, new CreatePSBTRequest
            {
                FeePreference = new FeePreference { ExplicitFeeRate = new FeeRate(5m) },
                Destinations = { new CreatePSBTDestination { Destination = address, Amount = Money.Satoshis(50_000) } }
            }, token)).PSBT;
            originalPsbt = await account.Merchant.Sign(originalPsbt);
            originalPsbt.Finalize();
        }
        var original = originalPsbt.ExtractTransaction();
        using var services = TestServices.Initialize();
        services.WaitForServicesReady();
        var store = tester.PayTester.GetService<PayjoinReceiverSessionStore>();
        using var transport = new NativeSeenInputTestSupport(services, store);
        using var keys = services.FetchOhttpKeys();
        using var builder = new ReceiverBuilder(address.ToString(), services.DirectoryUrl(), keys);
        using var withAmount = builder.WithAmount(50_000);
        using var bootstrap = withAmount.Build();
        var capture = new CapturingReceiverSessionPersister();
        using var receiver = bootstrap.Save(capture);
        var invoiceId = $"fallback-{Guid.NewGuid():N}";
        var expiry = DateTimeOffset.UtcNow.AddMinutes(20);
        var session = store.GetOrCreateSession(invoiceId, address.ToString(), account.Merchant.StoreId, expiry, capture.Events,
            new CreatePayjoinAccountingBridgeRequest(invoiceId, account.Merchant.StoreId, "BTC", "BTC-CHAIN", expiry));
        var writer = store.CreatePersister(session);
        using var uri = receiver.PjUri();
        using var senderBuilder = new SenderBuilder(originalPsbt.ToBase64(), uri);
        using var senderBootstrap = senderBuilder.BuildRecommended(1000);
        var senderWriter = new SenderPersister();
        using var sender = senderBootstrap.Save(senderWriter);
        using var post = sender.CreateV2PostRequest(services.OhttpRelayUrl());
        var postBody = await transport.SendAsync(post.Request, token);
        using var postResponse = sender.ProcessResponse(postBody, post.OhttpCtx);
        using var pollingSender = postResponse.Save(senderWriter);
        using var poll = receiver.CreatePollRequest(new UriBuilder(services.OhttpRelayUrl()) { Scheme = "https" }.Uri.AbsoluteUri);
        var responseBody = await transport.SendAsync(poll.Request, token);
        using var response = receiver.ProcessResponse(responseBody, poll.ClientResponse);
        using var outcome = response.Save(writer);
        var payload = Assert.IsType<InitializedTransitionOutcome.Progress>(outcome).Inner;
        using var interactive = payload.AssumeInteractiveReceiver();
        using var owned = interactive.Save(writer);
        using var cancellation = owned.Cancel();
        using var pending = cancellation.Save(writer);
        Assert.NotNull(pending);
        Assert.Equal(original.ToBytes(), pending.FallbackTx());
        var bridge = tester.PayTester.GetService<IPayjoinAccountingBridgeService>();
        var before = await bridge.TryGetByInvoiceIdAsync(invoiceId, token);
        Assert.Equal(PayjoinAccountingBridgeStatus.PendingFallback, before!.Status);
        Assert.Null(before.FallbackTransactionId);
        Assert.Null(before.ExpectedFinalTransactionId);
        var context = new PayjoinReceiverStateContext(session, writer, address.ScriptPubKey.ToBytes(),
            account.Merchant.StoreId, invoiceId, _ => false);
        var clock = new ManualTimeProvider();
        var broadcasts = 0;
        BroadcastResult? lastResult = null;
        var processor = new PayjoinReceiverStateProcessor(store, null!, null!, null!, null!,
            async (bytes, cancellationToken) =>
            {
                Assert.Equal(original.ToBytes(), bytes);
                broadcasts++;
                lastResult = await client.BroadcastAsync(Transaction.Load(bytes, Network.RegTest), cancellationToken).ConfigureAwait(false);
                TestLogs.LogInformation($"Fallback broadcast {broadcasts} in scenario {scenario}: success={lastResult.Success}, code={lastResult.RPCCode}, message={lastResult.RPCMessage}.");
                return lastResult;
            }, tester.PayTester.GetService<ILogger<PayjoinReceiverStateProcessor>>(), timeProvider: clock);

        await processor.ProcessPendingFallbackAsync(context, pending, token);
        Assert.Equal(1, broadcasts);
        if (scenario != "success")
        {
            Assert.False(lastResult!.Success);
            Assert.Equal(scenario == "non-final" ? NBitcoin.RPC.RPCErrorCode.RPC_TRANSACTION_REJECTED : NBitcoin.RPC.RPCErrorCode.RPC_TRANSACTION_ERROR,
                lastResult.RPCCode);
            if (scenario == "non-final") Assert.Contains("non-final", lastResult.RPCMessage, StringComparison.Ordinal);
            TestLogs.LogInformation($"Original fallback {original.GetHash()} initially deferred in scenario {scenario}: {lastResult.RPCCode} {lastResult.RPCMessage}.");
            AssertState<ReceiveSession.ReceiverPendingFallback>(store, invoiceId, session.SessionInstanceId);
            Assert.Equal(before, await bridge.TryGetByInvoiceIdAsync(invoiceId, token));
            if (parent is not null)
            {
                var parentResult = await client.BroadcastAsync(parent, token);
                Assert.True(parentResult.Success);
            }
            else if (scenario == "non-final") await tester.ExplorerNode.GenerateAsync(1, token);
            for (var tick = 0; tick < 3; tick++) await processor.ProcessPendingFallbackAsync(context, pending, token);
            clock.Advance(PayjoinReceiverStateProcessor.FallbackRetryDelay - TimeSpan.FromMilliseconds(1));
            await processor.ProcessPendingFallbackAsync(context, pending, token);
            Assert.Equal(1, broadcasts);
            AssertState<ReceiveSession.ReceiverPendingFallback>(store, invoiceId, session.SessionInstanceId);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            await processor.ProcessPendingFallbackAsync(context, pending, token);
            Assert.Equal(2, broadcasts);
        }

        if (scenario == "missing-inputs") AssertState<ReceiveSession.ReceiverPendingFallback>(store, invoiceId, session.SessionInstanceId);
        else AssertState<ReceiveSession.Closed>(store, invoiceId, session.SessionInstanceId);
        Assert.Equal(before, await bridge.TryGetByInvoiceIdAsync(invoiceId, token));
        var mempool = await tester.ExplorerNode.GetRawMempoolAsync(token);
        if (scenario == "missing-inputs")
        {
            Assert.DoesNotContain(original.GetHash(), mempool);
            var rejected = await client.BroadcastAsync(original, token);
            Assert.False(rejected.Success);
            Assert.Equal(NBitcoin.RPC.RPCErrorCode.RPC_TRANSACTION_ERROR, rejected.RPCCode);
            TestLogs.LogInformation($"Fallback {original.GetHash()} remains pending after structured rejection {rejected.RPCCode}.");
        }
        else
        {
            Assert.Contains(original.GetHash(), mempool);
            var actual = await tester.ExplorerNode.GetRawTransactionAsync(original.GetHash(), cancellationToken: token);
            Assert.Equal(original.ToHex(), actual.ToHex());
            var repeated = await client.BroadcastAsync(original, token);
            Assert.True(repeated.Success || repeated.RPCCode == NBitcoin.RPC.RPCErrorCode.RPC_TRANSACTION_ALREADY_IN_CHAIN);
            await tester.ExplorerNode.GenerateAsync(1, token);
            TestLogs.LogInformation($"Original fallback {original.GetHash()} recovered in scenario {scenario}, published and safely repeated.");
        }
    }

    private static void AssertState<T>(PayjoinReceiverSessionStore store, string invoiceId, Guid generation)
    {
        using var replay = PayjoinMethods.ReplayReceiverEventLog(store.CreatePersister(store.GetAttempt(invoiceId, generation)));
        using var state = replay.State();
        Assert.IsType<T>(state);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan time) => _now += time;
    }

    private sealed class SenderPersister : JsonSenderSessionPersister
    {
        private readonly List<string> _events = [];
        public void Save(string @event) => _events.Add(@event);
        public string[] Load() => _events.ToArray();
        public void Close() { }
    }
}
