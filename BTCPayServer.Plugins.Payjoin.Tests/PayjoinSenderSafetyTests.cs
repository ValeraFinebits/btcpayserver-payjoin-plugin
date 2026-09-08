using BTCPayServer.Logging;
using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NBXplorer;
using Payjoin;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class PayjoinSenderSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DispatchClaimAndCancellationCannotBothWinTheDatabaseRace(bool dispatchWins)
    {
        using var database = new RelationalPluginTestContext();
        var store = database.CreateSenderStore();
        var session = CreateSession(store);
        bool Cancel() => store.CompleteSession(session.SenderSessionId, PayjoinSenderSessionStatus.Failed, null, "cancelled", requireUnshared: true);
        bool Dispatch() => store.TryMarkPaymentExposed(session.SenderSessionId);
        database.BeforeSaveChanges = () =>
        {
            database.BeforeSaveChanges = null;
            Assert.True(dispatchWins ? Dispatch() : Cancel());
        };
        Assert.False(dispatchWins ? Cancel() : Dispatch());
        Assert.True(store.TryGetSession(session.SenderSessionId, out var current));
        Assert.Equal(dispatchWins, current!.PaymentExposed);
        Assert.Equal(dispatchWins ? PayjoinSenderSessionStatus.Pending : PayjoinSenderSessionStatus.Failed, current.Status);
        Assert.Equal(dispatchWins ? 1 : 0, store.GetOutpointsHeldByLiveSessions("store").Count);
    }

    [Fact]
    public async Task CancellationWinsBeforeAStaleWorkerCanDispatch()
    {
        using var database = new RelationalPluginTestContext();
        var store = database.CreateSenderStore();
        var stale = CreateSession(store);
        var relay = new GatedRelay();
        var processor = CreateProcessor(store, relay);
        Assert.True((await processor.CancelAsync("store", stale.SenderSessionId, CancellationToken.None)).Success);
        await processor.ProcessSessionGuardedAsync(stale, CancellationToken.None);
        Assert.False(relay.Entered.Task.IsCompleted);
        Assert.False(store.TryMarkPaymentExposed(stale.SenderSessionId));
        Assert.Empty(store.GetOutpointsHeldByLiveSessions("store"));
    }

    [Fact]
    public async Task InFlightAndLostPostResponsesNeverMakeCoinsSafelyReusable()
    {
        using var database = new RelationalPluginTestContext();
        var store = database.CreateSenderStore();
        var session = CreateSession(store);
        var relay = new GatedRelay();
        var processor = CreateProcessor(store, relay);
        var processing = processor.ProcessSessionGuardedAsync(session, CancellationToken.None);
        await relay.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);
        try
        {
            Assert.False((await processor.CancelAsync("store", session.SenderSessionId, CancellationToken.None)).Success);
            Assert.True(processor.HasBeenShared(session.SenderSessionId));
            Assert.Single(store.GetOutpointsHeldByLiveSessions("store"));
        }
        finally { relay.Resume.TrySetResult(); }
        await processing.ConfigureAwait(true);
        Assert.True(relay.RequestBytes > 0);

        // A fresh store/processor sees the marker despite replay still being WithReplyKey.
        var restartedStore = database.CreateSenderStore();
        var restarted = CreateProcessor(restartedStore, relay);
        Assert.False((await restarted.CancelAsync("store", session.SenderSessionId, CancellationToken.None)).Success);
        Assert.True(restartedStore.TryGetSession(session.SenderSessionId, out var current));
        Assert.Equal(PayjoinSenderSessionStatus.Pending, current!.Status);
        Assert.Single(restartedStore.GetOutpointsHeldByLiveSessions("store"));
        using var replay = PayjoinMethods.ReplaySenderEventLog(restartedStore.CreatePersister(session.SenderSessionId));
        using var state = replay.State();
        Assert.IsType<SendSession.WithReplyKey>(state);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StorageFailureRetriesWithoutFallback(bool throughFfi)
    {
        using var database = new RelationalPluginTestContext();
        var store = database.CreateSenderStore();
        var session = CreateSession(store);
        var relay = new GatedRelay { FailThroughFfi = throughFfi, Store = store };
        relay.Resume.SetResult();
        database.FailSaveChanges = !throughFfi;
        // No broadcaster is supplied: any accidental fallback makes this call fail.
        await CreateProcessor(store, relay).ProcessSessionGuardedAsync(session, CancellationToken.None);
        database.FailSaveChanges = false;
        Assert.True(store.TryGetSession(session.SenderSessionId, out var current));
        Assert.Equal(PayjoinSenderSessionStatus.Pending, current!.Status);
        Assert.Equal(session.Events, current.Events);
        Assert.Single(store.GetOutpointsHeldByLiveSessions("store"));
    }

    [Fact]
    public async Task IndependentSessionsStillProgressWhileOneRelayIsStalled()
    {
        using var database = new RelationalPluginTestContext();
        var store = database.CreateSenderStore();
        var first = CreateSession(store, "first");
        var second = CreateSession(store, "second");
        var slow = new GatedRelay();
        var fast = new GatedRelay();
        fast.Resume.SetResult();
        var processing = CreateProcessor(store, slow).ProcessSessionGuardedAsync(first, CancellationToken.None);
        await slow.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);
        try
        {
            await CreateProcessor(store, fast).ProcessSessionGuardedAsync(second, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.True(fast.Entered.Task.IsCompleted);
            Assert.False(processing.IsCompleted);
        }
        finally { slow.Resume.TrySetResult(); }
        await processing.ConfigureAwait(true);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-transaction")]
    public async Task AnExposedPaymentWithAnUnreadableFallbackKeepsItsCoinsReserved(string? originalHex)
    {
        using var database = new RelationalPluginTestContext();
        var store = database.CreateSenderStore();
        var session = CreateSession(store);
        Assert.True(store.TryMarkPaymentExposed(session.SenderSessionId));
        using (var context = database.CreateDbContext())
        {
            var row = context.SenderSessions.Single();
            row.OriginalTransactionHex = originalHex;
            context.SaveChanges();
        }
        Assert.True(store.TryGetSession(session.SenderSessionId, out var current));
        Assert.Equal(PayjoinSenderTerminalOutcome.RetryLater,
            await PayjoinSenderSessionTerminator.EndAsync(store, null!, null!, Network.TestNet,
                current!, "replay failed", CancellationToken.None));
        Assert.Single(store.GetOutpointsHeldByLiveSessions("store"));
    }

    internal static PayjoinSenderSessionState CreateSession(PayjoinSenderSessionStore store, string id = "sender")
    {
        using var keys = OhttpKeys.Decode(Convert.FromHexString("01001604ba48c49c3d4a92a3ad00ecc63a024da10ced02180c73ec12d8a7ad2cc91bb483824fe2bee8d28bfe2eb2fc6453bc4d31cd851e8a6540e86c5382af588d370957000400010003"));
        using var receiverBuilder = new ReceiverBuilder("2MuyMrZHkbHbfjudmKUy45dU4P17pjG2szK", "https://example.com", keys);
        using var receiverTransition = receiverBuilder.Build();
        using var receiver = receiverTransition.Save(new CapturingReceiverSessionPersister());
        using var uri = receiver.PjUri();
        var bootstrap = new CapturingSenderSessionPersister();
        using var builder = new SenderBuilder(PayjoinMethods.OriginalPsbt(), uri);
        using var transition = builder.BuildRecommended(1000);
        using var sender = transition.Save(bootstrap);
        var psbt = PSBT.Parse(PayjoinMethods.OriginalPsbt(), Network.TestNet);
        Assert.True(psbt.TryFinalize(out _));
        return store.CreateSession(id, "store", $"bitcoin:{id}", "test", 1000, psbt.GetGlobalTransaction().GetHash().ToString(),
            bootstrap.Load(), outpointsUsed: [$"{id}:0"], originalTransactionHex: psbt.ExtractTransaction().ToHex());
    }

    internal static PayjoinSenderSessionProcessor CreateProcessor(PayjoinSenderSessionStore store, IPayjoinReceiverRelayRequestSender? relay = null)
    {
        var nbx = new NBXplorerNetworkProvider(ChainName.Testnet);
        var network = new BTCPayNetwork { CryptoCode = "BTC", NBXplorerNetwork = nbx.GetFromCryptoCode("BTC"), DefaultSettings = new BTCPayDefaultSettings() };
        var networks = new BTCPayNetworkProvider([network], nbx, new Logs());
        return new PayjoinSenderSessionProcessor(store, relay!, networks, null!, null!, null!, null!, null!, NullLogger<PayjoinSenderSessionProcessor>.Instance);
    }

    private sealed class GatedRelay : IPayjoinReceiverRelayRequestSender
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RequestBytes { get; private set; }
        public bool FailThroughFfi { get; init; }
        public PayjoinSenderSessionStore? Store { get; init; }

        public async Task<(byte[] ResponseBody, TRequestContext RequestContext)> SendAsync<TRequestContext>(
            string storeId, string sessionId, Func<string, TRequestContext> buildRequest,
            Func<TRequestContext, (System.Uri Url, string ContentType, byte[] Body)> describeRequest,
            CancellationToken cancellationToken) where TRequestContext : IDisposable
        {
            Entered.TrySetResult();
            await Resume.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (FailThroughFfi)
            {
                using var replay = PayjoinMethods.ReplaySenderEventLog(Store!.CreatePersister(sessionId));
                using var state = replay.State();
                using var transition = Assert.IsType<SendSession.WithReplyKey>(state).Inner.Cancel();
                using var saved = transition.Save(new FailingPersister());
            }
            using var request = buildRequest("https://relay.example/");
            RequestBytes = describeRequest(request).Body.Length;
            throw new HttpRequestException("The request was accepted, but its response was lost.");
        }
    }

    private sealed class FailingPersister : JsonSenderSessionPersister
    {
        public void Save(string value) => throw new DbUpdateException("Injected storage failure");
        public string[] Load() => [];
        public void Close() { }
    }
}
