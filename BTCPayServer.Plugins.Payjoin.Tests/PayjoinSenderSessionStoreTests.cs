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
        Assert.True(store.HasPendingSessionForBip21(created.Bip21));
        Assert.False(store.HasPendingSessionForBip21("bitcoin:tb1qother?amount=0.001&pj=https://example.test/#K1"));

        // A session that ended releases the URI, so a failed payment can be retried.
        Assert.True(store.CompleteSession("session-uri", PayjoinSenderSessionStatus.Failed, null, "relay unreachable"));
        Assert.False(store.HasPendingSessionForBip21(created.Bip21));
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

        Assert.True(store.StartSignedSession("session-signed", ["bootstrap-event"]));

        var started = Assert.Single(store.GetPendingSessions());
        Assert.Equal("session-signed", started.SenderSessionId);
        Assert.Equal(PayjoinSenderSessionStatus.Pending, started.Status);
        Assert.Equal(["bootstrap-event"], started.Events);
        Assert.Null(started.PendingTransactionId);

        // A repeated signature event must not seed the state twice.
        Assert.False(store.StartSignedSession("session-signed", ["bootstrap-event"]));
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
    public void CompleteSessionRejectsAwaitingSignature()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();
        CreateAwaitingSignatureSession(store, "session-not-terminal", "pending-not-terminal");

        Assert.Throws<ArgumentException>(() =>
            store.CompleteSession("session-not-terminal", PayjoinSenderSessionStatus.AwaitingSignature, null, null));
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
            ["bootstrap-event"]);
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
