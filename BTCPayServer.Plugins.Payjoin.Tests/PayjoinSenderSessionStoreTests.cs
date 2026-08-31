using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class PayjoinSenderSessionStoreTests
{
    private const string OriginalTxId = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const long FeeRateSatPerKwu = 1250;
    // Each session spends its own coin: the outpoint table's primary key holds even on the
    // InMemory provider, so two sessions must not share an outpoint unless the test wants the
    // refusal itself.
    private static string OutpointFor(string senderSessionId) => $"{senderSessionId}-outpoint:0";
    // A minimal consensus-valid transaction; the store keeps this opaque, so its shape is free.
    private const string SignedOriginalHex = "02000000000101cccc00000000";

    [Fact]
    public void CreateSessionRoundTripsThroughFreshStoreInstance()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();

        var created = CreateSession(store, "session-create");

        var reloadedStore = testContext.CreateStore();
        Assert.True(reloadedStore.TryGetSession("session-create", out var reloaded));
        Assert.NotNull(reloaded);
        Assert.Equal(created.StoreId, reloaded!.StoreId);
        Assert.Equal(created.Bip21, reloaded.Bip21);
        Assert.Equal(created.OriginalTransactionId, reloaded.OriginalTransactionId);
        Assert.Equal(PayjoinSenderSessionStatus.Pending, reloaded.Status);
    }

    [Fact]
    public void PersistedEventsReplayInOrderAcrossFreshStoreInstances()
    {
        using var testContext = new TestContext();
        var firstStore = testContext.CreateStore();
        CreateSession(firstStore, "session-events");

        var firstPersister = firstStore.CreatePersister("session-events");
        firstPersister.Save("event-1");

        var secondStore = testContext.CreateStore();
        var secondPersister = secondStore.CreatePersister("session-events");
        Assert.Equal(new[] { "bootstrap-event", "event-1" }, secondPersister.Load());

        secondPersister.Save("event-2");
        Assert.Equal(new[] { "bootstrap-event", "event-1", "event-2" }, secondPersister.Load());
    }

    [Fact]
    public void PendingSessionsExcludeTerminalStates()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();
        CreateSession(store, "session-pending");
        CreateSession(store, "session-done", originalTransactionId: "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd");

        Assert.True(store.CompleteSession("session-done", PayjoinSenderSessionStatus.CompletedPayjoin, "eeee", failureMessage: null));

        var pending = store.GetPendingSessions();
        var pendingSession = Assert.Single(pending);
        Assert.Equal("session-pending", pendingSession.SenderSessionId);
    }

    [Fact]
    public void HasPendingSessionForOriginalGuardsDoublePaymentUntilCompletion()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();
        CreateSession(store, "session-dedup");

        Assert.True(store.HasPendingSessionForOriginal(OriginalTxId));

        Assert.True(store.CompleteSession("session-dedup", PayjoinSenderSessionStatus.CompletedFallback, OriginalTxId, failureMessage: null));

        Assert.False(store.HasPendingSessionForOriginal(OriginalTxId));
    }

    [Fact]
    public void HasPendingSessionForBip21CatchesARepeatedSubmissionOfTheSameUri()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();
        var created = CreateSession(store, "session-uri");

        // A second attempt on one URI can select different coins, so the URI is what identifies
        // the payment.
        Assert.True(store.HasPendingSessionForBip21(created.StoreId, created.Bip21));
        Assert.False(store.HasPendingSessionForBip21(created.StoreId, "bitcoin:tb1qother?amount=0.001&pj=https://example.test/#K1"));

        // A session that ended releases the URI, so a failed payment can be retried.
        Assert.True(store.CompleteSession("session-uri", PayjoinSenderSessionStatus.Failed, null, "relay unreachable"));
        Assert.False(store.HasPendingSessionForBip21(created.StoreId, created.Bip21));
    }

    [Fact]
    public void CompleteSessionRecordsTerminalStateAndRejectsPending()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();
        CreateSession(store, "session-complete");

        Assert.Throws<ArgumentException>(() =>
            store.CompleteSession("session-complete", PayjoinSenderSessionStatus.Pending, null, null));

        Assert.True(store.CompleteSession("session-complete", PayjoinSenderSessionStatus.Failed, null, "relay unreachable"));
        Assert.True(store.TryGetSession("session-complete", out var completed));
        Assert.Equal(PayjoinSenderSessionStatus.Failed, completed!.Status);
        Assert.Equal("relay unreachable", completed.FailureMessage);
        Assert.Null(completed.BroadcastTransactionId);
    }

    [Fact]
    public void AwaitingSignatureSessionCarriesNoLibraryStateAndIsHiddenFromThePoller()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();

        var created = CreateAwaitingSignatureSession(store, "session-cold", "pending-1");

        Assert.Equal(PayjoinSenderSessionStatus.AwaitingSignature, created.Status);
        Assert.Empty(created.Events);
        // The poller drives the library state machine, and this session has no state yet.
        Assert.Empty(store.GetPendingSessions());
        // The coins are already committed, so the same URI must not start a second session.
        Assert.True(store.HasPendingSessionForOriginal(OriginalTxId));
    }

    [Fact]
    public void SignatureLookupFindsTheSessionThatAskedForIt()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();
        CreateAwaitingSignatureSession(store, "session-lookup", "pending-lookup");

        Assert.True(store.TryGetSessionByPendingTransactionId("pending-lookup", out var found));
        Assert.Equal("session-lookup", found!.SenderSessionId);
        Assert.Equal("https://example.test/", found.RequestBaseUrl);

        Assert.False(store.TryGetSessionByPendingTransactionId("pending-unknown", out var missing));
        Assert.Null(missing);
    }

    [Fact]
    public void StartSignedSessionSeedsLibraryStateAndReleasesThePendingTransaction()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();
        CreateAwaitingSignatureSession(store, "session-signed", "pending-signed");

        Assert.True(store.StartSignedSession("session-signed", ["bootstrap-event"], SignedOriginalHex));

        var started = Assert.Single(store.GetPendingSessions());
        Assert.Equal("session-signed", started.SenderSessionId);
        Assert.Equal(PayjoinSenderSessionStatus.Pending, started.Status);
        Assert.Equal(["bootstrap-event"], started.Events);
        Assert.Null(started.PendingTransactionId);
        // The signed original is the session's own fallback copy from here on.
        Assert.Equal(SignedOriginalHex, started.OriginalTransactionHex);

        // A repeated signature event must not seed the state twice.
        Assert.False(store.StartSignedSession("session-signed", ["bootstrap-event"], SignedOriginalHex));
        Assert.Equal(["bootstrap-event"], store.CreatePersister("session-signed").Load());
    }

    [Fact]
    public void AwaitSignatureParksARunningSessionOnASecondPendingTransaction()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();
        CreateSession(store, "session-second-round");

        Assert.True(store.AwaitSignature("session-second-round", "pending-proposal"));

        Assert.Empty(store.GetPendingSessions());
        Assert.True(store.TryGetSession("session-second-round", out var parked));
        Assert.Equal(PayjoinSenderSessionStatus.AwaitingSignature, parked!.Status);
        Assert.Equal("pending-proposal", parked.PendingTransactionId);
        // The event log survives, so the poller resumes where it stopped once the payjoin is out.
        Assert.Equal(["bootstrap-event"], parked.Events);

        // Only a running session parks; a session already waiting must not move again.
        Assert.False(store.AwaitSignature("session-second-round", "pending-other"));
    }

    [Fact]
    public void TheFirstTerminalStateWins()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();
        CreateAwaitingSignatureSession(store, "session-race", "pending-race");

        // The operator stops a session while a signature collected a moment earlier is still on
        // its way. Whichever landed first is what happened.
        Assert.True(store.CompleteSession("session-race", PayjoinSenderSessionStatus.CompletedFallback, "aaaa", null));
        Assert.False(store.CompleteSession("session-race", PayjoinSenderSessionStatus.CompletedPayjoin, "bbbb", null));

        Assert.True(store.TryGetSession("session-race", out var session));
        Assert.Equal(PayjoinSenderSessionStatus.CompletedFallback, session!.Status);
        Assert.Equal("aaaa", session.BroadcastTransactionId);
        // The handle to the signing request survives completion so the release step can
        // withdraw the row; the terminal status is what turns a late signature away, and the
        // release clears the handle afterwards.
        Assert.Equal("pending-race", session.PendingTransactionId);
        store.ClearReleasedResources("session-race");
        Assert.True(store.TryGetSession("session-race", out var released));
        Assert.Null(released!.PendingTransactionId);
        Assert.False(store.TryGetSessionByPendingTransactionId("pending-race", out _));
    }

    [Fact]
    public void LiveSessionsHoldTheirCoinsUntilTheyEnd()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();
        CreateSession(store, "session-coins");

        // Nothing else this store builds may spend what a live session already committed, even
        // though a hot-wallet session creates no pending transaction to hold them.
        Assert.Equal([OutpointFor("session-coins")], store.GetOutpointsHeldByLiveSessions("store-1"));
        Assert.Empty(store.GetOutpointsHeldByLiveSessions("store-other"));

        Assert.True(store.CompleteSession("session-coins", PayjoinSenderSessionStatus.CompletedPayjoin, "eeee", null));
        Assert.Empty(store.GetOutpointsHeldByLiveSessions("store-1"));
    }

    [Fact]
    public void SessionsAwaitingSignatureAreListedForTheSweep()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();
        CreateSession(store, "session-live");
        CreateAwaitingSignatureSession(store, "session-waiting", "pending-waiting");

        // The signature arrives as an in-memory event that a restart can lose, so the poller has
        // to be able to find these sessions on its own.
        var waiting = Assert.Single(store.GetSessionsAwaitingSignature());
        Assert.Equal("session-waiting", waiting.SenderSessionId);
        Assert.Equal("pending-waiting", waiting.PendingTransactionId);
    }

    [Fact]
    public void SessionsCarryTheFeeRateTheSecondRoundNeeds()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();
        CreateAwaitingSignatureSession(store, "session-fee", "pending-fee");

        // The poller drives the second round with no access to the original request, so the rate
        // the operator chose has to survive on the session itself.
        Assert.True(store.TryGetSession("session-fee", out var session));
        Assert.Equal(FeeRateSatPerKwu, session!.FeeRateSatPerKwu);
    }

    [Fact]
    public void CompleteSessionRejectsAwaitingSignature()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();
        CreateAwaitingSignatureSession(store, "session-not-terminal", "pending-not-terminal");

        Assert.Throws<ArgumentException>(() =>
            store.CompleteSession("session-not-terminal", PayjoinSenderSessionStatus.AwaitingSignature, null, null));
    }

    [Fact]
    public void AwaitSignatureCannotResurrectACompletedSession()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();
        CreateSession(store, "session-resurrect");

        Assert.True(store.CompleteSession("session-resurrect", PayjoinSenderSessionStatus.Failed, null, "test"));

        // A poller that read the session as running a moment ago parks it after the operator
        // stopped it. The park must lose; a completed session stays completed.
        Assert.False(store.AwaitSignature("session-resurrect", "pending-late"));
        Assert.True(store.TryGetSession("session-resurrect", out var session));
        Assert.Equal(PayjoinSenderSessionStatus.Failed, session!.Status);
    }

    [Fact]
    public void ASecondSessionForTheSameCoinIsRefused()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();
        var first = CreateSession(store, "session-coin-a");

        // A different URI and a different store change nothing: the coin is the resource, and
        // stores can share a wallet, so the guard is global.
        Assert.Throws<PayjoinSenderDuplicateSessionException>(() => store.CreateSession(
            "session-coin-b",
            "store-2",
            "bitcoin:tb1qother?amount=0.002&pj=https://example.test/#K1",
            "tb1qother",
            200_000,
            "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
            ["bootstrap-event"],
            FeeRateSatPerKwu,
            first.OutpointsUsed));

        // A finished session frees its coins for the next payment.
        Assert.True(store.CompleteSession("session-coin-a", PayjoinSenderSessionStatus.CompletedPayjoin, "eeee", null));
        store.CreateSession(
            "session-coin-c",
            "store-2",
            "bitcoin:tb1qthird?amount=0.003&pj=https://example.test/#K1",
            "tb1qthird",
            300_000,
            "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
            ["bootstrap-event"],
            FeeRateSatPerKwu,
            first.OutpointsUsed);
    }

    [Fact]
    public void AParkedSessionKeepsItsCoinReservationVisibleToTheSweep()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();
        store.CreateSession(
            "session-parked",
            "store-1",
            "bitcoin:tb1qparked?amount=0.001&pj=https://example.test/#K1",
            "tb1qparked",
            100_000,
            OriginalTxId,
            [],
            FeeRateSatPerKwu,
            [OutpointFor("session-parked")],
            originalTransactionHex: "00",
            pendingTransactionId: "pending-proposal",
            PayjoinSenderSessionStatus.AwaitingSignature,
            requestBaseUrl: "https://example.test/",
            coinReservationTransactionId: "reservation-parked");

        // The operator can broadcast or cancel the reserved plain payment while the session
        // waits for its second signature, so the sweep must still see it.
        var live = Assert.Single(store.GetLiveSessionsWithCoinReservations());
        Assert.Equal("session-parked", live.SenderSessionId);
        Assert.Equal("reservation-parked", live.CoinReservationTransactionId);
    }

    [Fact]
    public void DanglingResourcesAreListedUntilCleared()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();
        CreateAwaitingSignatureSession(store, "session-dangling", "pending-dangling");

        Assert.Empty(store.GetSessionsWithDanglingResources());
        Assert.True(store.CompleteSession("session-dangling", PayjoinSenderSessionStatus.Failed, null, "test"));

        // Completion keeps the handles so the release can withdraw the rows; the sweep lists
        // the session until the release records itself by clearing them.
        var dangling = Assert.Single(store.GetSessionsWithDanglingResources());
        Assert.Equal("session-dangling", dangling.SenderSessionId);
        store.ClearReleasedResources("session-dangling");
        Assert.Empty(store.GetSessionsWithDanglingResources());
    }

    private static PayjoinSenderSessionState CreateSession(
        PayjoinSenderSessionStore store,
        string senderSessionId,
        string originalTransactionId = OriginalTxId)
    {
        return store.CreateSession(
            senderSessionId,
            "store-1",
            "bitcoin:tb1q?amount=0.001&pj=https://example.test/#K1",
            "tb1q",
            100_000,
            originalTransactionId,
            ["bootstrap-event"],
            FeeRateSatPerKwu,
            [OutpointFor(senderSessionId)]);
    }

    private static PayjoinSenderSessionState CreateAwaitingSignatureSession(
        PayjoinSenderSessionStore store,
        string senderSessionId,
        string pendingTransactionId)
    {
        return store.CreateSession(
            senderSessionId,
            "store-1",
            "bitcoin:tb1q?amount=0.001&pj=https://example.test/#K1",
            "tb1q",
            100_000,
            OriginalTxId,
            [],
            FeeRateSatPerKwu,
            [OutpointFor(senderSessionId)],
            null,
            pendingTransactionId,
            PayjoinSenderSessionStatus.AwaitingSignature,
            "https://example.test/");
    }

    private sealed class TestContext : IDisposable
    {
        private readonly TestPayjoinPluginDbContextFactory _dbContextFactory = new();
        private readonly PostgresPayjoinUniqueConstraintViolationDetector _uniqueConstraintViolationDetector = new();

        public PayjoinSenderSessionStore CreateStore() => new(_dbContextFactory, _uniqueConstraintViolationDetector);

        public void Dispose()
        {
            using var context = _dbContextFactory.CreateContext();
            context.Database.EnsureDeleted();
        }
    }

    private sealed class TestPayjoinPluginDbContextFactory : PayjoinPluginDbContextFactory
    {
        private static readonly InMemoryDatabaseRoot SharedDatabaseRoot = new();
        private readonly DbContextOptions<PayjoinPluginDbContext> _dbContextOptions;

        public TestPayjoinPluginDbContextFactory()
            : base(Options.Create(new DatabaseOptions
            {
                ConnectionString = "Host=localhost;Database=payjoin-plugin-tests;Username=postgres"
            }))
        {
            var databaseName = $"payjoin-plugin-tests-{Guid.NewGuid():N}";
            _dbContextOptions = new DbContextOptionsBuilder<PayjoinPluginDbContext>()
                .UseInMemoryDatabase(databaseName, SharedDatabaseRoot)
                .Options;

            using var context = CreateContext();
            context.Database.EnsureCreated();
        }

        public override PayjoinPluginDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptionsAction = null)
        {
            return new PayjoinPluginDbContext(_dbContextOptions);
        }
    }
}
