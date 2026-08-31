using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Tests;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests;

/// <summary>
/// The sender store's concurrency guards, on the real database. The unit project's InMemory
/// provider enforces no unique indexes, so these tests live here: without Postgres they would
/// pass against code whose constraints are broken or missing.
/// </summary>
[Collection(nameof(NonParallelizableCollectionDefinition))]
public class PayjoinSenderConcurrencyIntegrationTests : UnitTestBase
{
    public PayjoinSenderConcurrencyIntegrationTests(ITestOutputHelper helper) : base(helper)
    {
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task TwoSubmissionsOfOneUriCreateOneSession()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        await tester.StartAsync().WaitAsync(cts.Token).ConfigureAwait(true);
        var store = tester.PayTester.GetService<PayjoinSenderSessionStore>();

        const string uri = "bitcoin:bcrt1qfirst?amount=0.001&pj=https://example.test/#K1";
        CreateAwaitingSession(store, "uri-winner", uri);

        // The read-side check is advisory; the unique live-Bip21 index is the guard that holds
        // when two writers pass it together, and across processes and restarts.
        Assert.Throws<PayjoinSenderDuplicateSessionException>(() =>
            CreateAwaitingSession(store, "uri-loser", uri));

        // Another store paying the same URI is its own payment: the live index is scoped by
        // store, so only a duplicate within one store is refused.
        CreateAwaitingSession(store, "uri-other-store", uri, storeId: "store-concurrency-b");

        // A terminal session frees the URI for the next payment.
        Assert.True(store.CompleteSession("uri-winner", PayjoinSenderSessionStatus.Failed, null, "test"));
        CreateAwaitingSession(store, "uri-after-completion", uri);

        // Under a genuine race, exactly one writer wins.
        const string racedUri = "bitcoin:bcrt1qraced?amount=0.002&pj=https://example.test/#K1";
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(() =>
        {
            try
            {
                CreateAwaitingSession(store, $"raced-{i}", racedUri);
                return true;
            }
            catch (PayjoinSenderDuplicateSessionException)
            {
                return false;
            }
        }, cts.Token))).ConfigureAwait(true);
        Assert.Single(outcomes, outcome => outcome);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task ConcurrentStartSignedSessionLetsExactlyOneWinnerThrough()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        await tester.StartAsync().WaitAsync(cts.Token).ConfigureAwait(true);
        var store = tester.PayTester.GetService<PayjoinSenderSessionStore>();

        // The race is real: the signature listener and the reconcile sweep can both react to
        // the same signed original. The status guard turns the late one away, and when both
        // pass it together, the unique (session, sequence) event index lets exactly one seed
        // the session; the loser's save rolls back and it reports false.
        CreateAwaitingSession(store, "signed-race", "bitcoin:bcrt1qsigned?amount=0.001&pj=https://example.test/#K1");
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(
            () => store.StartSignedSession("signed-race", ["bootstrap-event"], "00"),
            cts.Token))).ConfigureAwait(true);
        Assert.Single(outcomes, outcome => outcome);

        Assert.True(store.TryGetSession("signed-race", out var session));
        Assert.Equal(PayjoinSenderSessionStatus.Pending, session!.Status);
        Assert.Equal(["bootstrap-event"], session.Events);
        // The signing round's row became the coin reservation rather than being dropped.
        Assert.Null(session.PendingTransactionId);
        Assert.Equal("pending-signed-race", session.CoinReservationTransactionId);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task ConcurrentAppendsLeaveTheLogReplayable()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        await tester.StartAsync().WaitAsync(cts.Token).ConfigureAwait(true);
        var store = tester.PayTester.GetService<PayjoinSenderSessionStore>();

        CreateAwaitingSession(store, "append-race", "bitcoin:bcrt1qappend?amount=0.001&pj=https://example.test/#K1");
        var persister = store.CreatePersister("append-race");

        // Two writers can append at once: the poller advancing the session and the listener
        // handling a signature. The unique (session, sequence) index makes the order durable,
        // and the append retries with the next sequence when it loses, so every event survives.
        const int writerCount = 8;
        const int eventsPerWriter = 5;
        await Task.WhenAll(Enumerable.Range(0, writerCount).Select(writer => Task.Run(() =>
        {
            for (var i = 0; i < eventsPerWriter; i++)
            {
                persister.Save($"writer-{writer}-event-{i}");
            }
        }, cts.Token))).ConfigureAwait(true);

        Assert.True(store.TryGetSession("append-race", out var session));
        Assert.Equal(writerCount * eventsPerWriter, session!.Events.Length);
        Assert.Equal(writerCount * eventsPerWriter, session.Events.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task BroadcastTreatsANodeDuplicateAsSuccess()
    {
        // Several routes can broadcast one sender transaction: the poller, the listener, a
        // retry, and the operator's own button. Only the first gets an accepting answer, so
        // the broadcaster must read the node's duplicate answers as success. This runs against
        // the real node to pin the reason strings the running Bitcoin Core version actually
        // returns, both for a mempool duplicate and for a mined one.
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        await tester.StartAsync().WaitAsync(cts.Token).ConfigureAwait(true);

        var network = tester.NetworkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode);
        Assert.NotNull(network);
        var explorerClient = tester.PayTester.GetService<BTCPayServer.ExplorerClientProvider>().GetExplorerClient(network);

        var address = await tester.ExplorerNode.GetNewAddressAsync(cts.Token).ConfigureAwait(true);
        var txId = await tester.ExplorerNode.SendToAddressAsync(address, Money.Coins(0.1m), cancellationToken: cts.Token).ConfigureAwait(true);
        var transaction = await tester.ExplorerNode.GetRawTransactionAsync(txId, cancellationToken: cts.Token).ConfigureAwait(true);

        // Still in the mempool: the second broadcast must come back as a success.
        var mempoolDuplicate = await PayjoinSenderBroadcaster
            .BroadcastAsync(explorerClient, transaction, cts.Token).ConfigureAwait(true);
        Assert.Equal(txId.ToString(), mempoolDuplicate);

        var rewardAddress = await tester.ExplorerNode.GetNewAddressAsync(cts.Token).ConfigureAwait(true);
        await tester.ExplorerNode.GenerateToAddressAsync(1, rewardAddress, cts.Token).ConfigureAwait(true);

        // Mined: the node answers differently, and that must be a success too.
        var minedDuplicate = await PayjoinSenderBroadcaster
            .BroadcastAsync(explorerClient, transaction, cts.Token).ConfigureAwait(true);
        Assert.Equal(txId.ToString(), minedDuplicate);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task BroadcastClassifiesASpentInputRejectionAsPermanent()
    {
        // A conflicting spend can never be accepted by retrying, and the session code decides
        // between retry and fallback on this classification, so it must match what the running
        // node actually answers: once for a mempool conflict, once for inputs a mined
        // transaction has spent.
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        await tester.StartAsync().WaitAsync(cts.Token).ConfigureAwait(true);

        var network = tester.NetworkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode);
        Assert.NotNull(network);
        var explorerClient = tester.PayTester.GetService<BTCPayServer.ExplorerClientProvider>().GetExplorerClient(network);

        var unspent = (await tester.ExplorerNode.ListUnspentAsync().WaitAsync(cts.Token).ConfigureAwait(true))
            .First(u => u.Amount > Money.Coins(0.001m));
        var winner = await BuildNodeSpendAsync(tester, network!, unspent.OutPoint, unspent.Amount, cts.Token).ConfigureAwait(true);
        var conflict = await BuildNodeSpendAsync(tester, network!, unspent.OutPoint, unspent.Amount, cts.Token).ConfigureAwait(true);

        await tester.ExplorerNode.SendRawTransactionAsync(winner, cts.Token).ConfigureAwait(true);

        // The winner sits in the mempool: the conflict is refused, and the refusal is final.
        var mempoolRejection = await Assert.ThrowsAsync<PayjoinSenderBroadcastException>(() =>
            PayjoinSenderBroadcaster.BroadcastAsync(explorerClient, conflict, cts.Token)).ConfigureAwait(true);
        Assert.True(mempoolRejection.Permanent, mempoolRejection.Message);

        var rewardAddress = await tester.ExplorerNode.GetNewAddressAsync(cts.Token).ConfigureAwait(true);
        await tester.ExplorerNode.GenerateToAddressAsync(1, rewardAddress, cts.Token).ConfigureAwait(true);

        // The winner is mined: the coins are gone, and that refusal is final too.
        var spentRejection = await Assert.ThrowsAsync<PayjoinSenderBroadcastException>(() =>
            PayjoinSenderBroadcaster.BroadcastAsync(explorerClient, conflict, cts.Token)).ConfigureAwait(true);
        Assert.True(spentRejection.Permanent, spentRejection.Message);
    }

    private static async Task<Transaction> BuildNodeSpendAsync(
        ServerTester tester,
        BTCPayNetwork network,
        OutPoint outpoint,
        Money inputValue,
        CancellationToken cancellationToken)
    {
        var destination = await tester.ExplorerNode.GetNewAddressAsync(cancellationToken).ConfigureAwait(true);
        var transaction = network.NBitcoinNetwork.CreateTransaction();
        transaction.Inputs.Add(new TxIn(outpoint));
        transaction.Outputs.Add(inputValue - Money.Coins(0.0002m), destination);
        var response = await tester.ExplorerNode
            .SendCommandAsync("signrawtransactionwithwallet", transaction.ToHex())
            .WaitAsync(cancellationToken).ConfigureAwait(true);
        return Transaction.Parse(response.Result["hex"]!.ToString(), network.NBitcoinNetwork);
    }

    private static void CreateAwaitingSession(PayjoinSenderSessionStore store, string senderSessionId, string bip21, string storeId = "store-concurrency")
    {
        store.CreateSession(
            senderSessionId,
            storeId,
            bip21,
            "bcrt1qdestination",
            100_000,
            $"txid-{senderSessionId}",
            [],
            feeRateSatPerKwu: 1250,
            outpointsUsed: [$"{senderSessionId}-outpoint:0"],
            originalTransactionHex: null,
            pendingTransactionId: $"pending-{senderSessionId}",
            PayjoinSenderSessionStatus.AwaitingSignature,
            requestBaseUrl: "http://127.0.0.1/");
    }
}
