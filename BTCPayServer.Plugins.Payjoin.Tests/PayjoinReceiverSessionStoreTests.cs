using BTCPayServer.Client.Models;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.Payjoin;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using NBitcoin;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using System;
using System.Linq;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class PayjoinReceiverSessionStoreTests
{
    [Fact]
    public void CreateSessionRoundTripsThroughFreshStoreInstance()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();

        var createdSession = CreateSession(store, "invoice-create", out var created);

        Assert.True(created);

        var reloadedStore = testContext.CreateStore();
        Assert.True(reloadedStore.TryGetSession(createdSession.InvoiceId, out var reloadedSession));
        Assert.NotNull(reloadedSession);
        Assert.Equal(createdSession.InvoiceId, reloadedSession!.InvoiceId);
        Assert.Equal(createdSession.StoreId, reloadedSession.StoreId);
        Assert.Equal(createdSession.ReceiverAddress, reloadedSession.ReceiverAddress);
        Assert.Equal(createdSession.OhttpRelayUrl, reloadedSession.OhttpRelayUrl);
        Assert.Equal(createdSession.MonitoringExpiresAt, reloadedSession.MonitoringExpiresAt);
    }

    [Fact]
    public void PersistedEventsReplayInOrderAcrossFreshStoreInstances()
    {
        using var testContext = new TestContext();
        var firstStore = testContext.CreateStore();
        var firstSession = CreateSession(firstStore, "invoice-events", out _);

        var firstPersister = firstStore.CreatePersister(firstSession);
        firstPersister.Save("event-1");

        var secondStore = testContext.CreateStore();
        Assert.True(secondStore.TryGetSession(firstSession.InvoiceId, out var secondSession));
        var secondPersister = secondStore.CreatePersister(secondSession!);

        Assert.Equal(new[] { "event-1" }, secondPersister.Load());

        secondPersister.Save("event-2");

        var thirdStore = testContext.CreateStore();
        Assert.True(thirdStore.TryGetSession(firstSession.InvoiceId, out var thirdSession));
        var thirdPersister = thirdStore.CreatePersister(thirdSession!);

        Assert.Equal(new[] { "event-1", "event-2" }, thirdPersister.Load());

        using var context = testContext.CreateDbContext();
        var persistedSequences = context.ReceiverSessionEvents
            .OrderBy(x => x.Sequence)
            .Select(x => x.Sequence)
            .ToArray();
        Assert.Equal(new[] { 1, 2 }, persistedSequences);
    }

    [Fact]
    public void ContributedInputRoundTripsThroughFreshStoreInstance()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();
        var session = CreateSession(store, "invoice-input", out _);
        var expectedOutPoint = new OutPoint(uint256.Parse("1111111111111111111111111111111111111111111111111111111111111111"), uint.MaxValue);

        Assert.True(store.TryPersistContributedInput(session.InvoiceId, expectedOutPoint));

        var reloadedStore = testContext.CreateStore();
        Assert.True(reloadedStore.TryGetSession(session.InvoiceId, out var reloadedSession));
        Assert.True(reloadedSession!.TryGetContributedInput(out var actualOutPoint));
        Assert.Equal(expectedOutPoint, actualOutPoint);
    }

    [Fact]
    public void PersistingSameContributedInputDoesNotAdvanceUpdatedAt()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();
        var session = CreateSession(store, "invoice-same-input", out _);
        var expectedOutPoint = new OutPoint(uint256.Parse("3333333333333333333333333333333333333333333333333333333333333333"), 3);

        Assert.True(store.TryPersistContributedInput(session.InvoiceId, expectedOutPoint));
        using var firstContext = testContext.CreateDbContext();
        var firstUpdatedAt = firstContext.ReceiverSessions
            .Single(x => x.InvoiceId == session.InvoiceId)
            .UpdatedAt;

        Assert.True(store.TryPersistContributedInput(session.InvoiceId, expectedOutPoint));
        using var secondContext = testContext.CreateDbContext();
        var secondUpdatedAt = secondContext.ReceiverSessions
            .Single(x => x.InvoiceId == session.InvoiceId)
            .UpdatedAt;

        Assert.Equal(firstUpdatedAt, secondUpdatedAt);
    }

    [Fact]
    public void ContributedInputAndEventsReplayTogetherThroughFreshStoreInstance()
    {
        using var testContext = new TestContext();
        var firstStore = testContext.CreateStore();
        var firstSession = CreateSession(firstStore, "invoice-input-events", out _);
        var expectedOutPoint = new OutPoint(uint256.Parse("2222222222222222222222222222222222222222222222222222222222222222"), 2);
        var firstPersister = firstStore.CreatePersister(firstSession);

        firstPersister.Save("event-before-input");
        Assert.True(firstStore.TryPersistContributedInput(firstSession.InvoiceId, expectedOutPoint));
        firstPersister.Save("event-after-input");

        var replayedStore = testContext.CreateStore();
        Assert.True(replayedStore.TryGetSession(firstSession.InvoiceId, out var replayedSession));
        Assert.True(replayedSession!.TryGetContributedInput(out var actualOutPoint));
        Assert.Equal(expectedOutPoint, actualOutPoint);

        var replayedPersister = replayedStore.CreatePersister(replayedSession);
        Assert.Equal(new[] { "event-before-input", "event-after-input" }, replayedPersister.Load());
    }

    [Fact]
    public void RemoveSessionDeletesSessionAndEvents()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();
        var session = CreateSession(store, "invoice-remove", out _);
        var persister = store.CreatePersister(session);
        persister.Save("event-1");

        Assert.True(store.RemoveSession(session.InvoiceId));

        var reloadedStore = testContext.CreateStore();
        Assert.False(reloadedStore.TryGetSession(session.InvoiceId, out _));

        using var context = testContext.CreateDbContext();
        Assert.Empty(context.ReceiverSessions);
        Assert.Empty(context.ReceiverSessionEvents);
    }

    [Fact]
    public void CloseRequestedInitializedPollConsumptionPersistsThroughFreshStoreInstance()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();
        var session = CreateSession(store, "invoice-close", out _);

        Assert.True(store.RequestClose(session.InvoiceId, InvoiceStatus.Expired));

        var closeRequestedStore = testContext.CreateStore();
        Assert.True(closeRequestedStore.TryGetSession(session.InvoiceId, out var closeRequestedSession));
        Assert.True(closeRequestedSession!.IsCloseRequested);
        Assert.Equal(InvoiceStatus.Expired, closeRequestedSession.CloseInvoiceStatus);
        Assert.True(closeRequestedSession.CanPollInitializedAfterCloseRequest());

        Assert.True(closeRequestedStore.TryConsumeInitializedPollAfterCloseRequest(session.InvoiceId));

        var consumedStore = testContext.CreateStore();
        Assert.True(consumedStore.TryGetSession(session.InvoiceId, out var consumedSession));
        Assert.True(consumedSession!.InitializedPollAfterCloseRequestConsumed);
        Assert.False(consumedSession.CanPollInitializedAfterCloseRequest());
        Assert.False(consumedStore.TryConsumeInitializedPollAfterCloseRequest(session.InvoiceId));
    }

    private static PayjoinReceiverSessionState CreateSession(PayjoinReceiverSessionStore store, string invoiceId, out bool created)
    {
        return store.CreateSession(
            invoiceId,
            "bcrt1qexampleaddress0000000000000000000000000",
            "store-1",
            new Uri("https://relay.example/"),
            DateTimeOffset.UtcNow.AddMinutes(15),
            out created);
    }

    private sealed class TestContext : IDisposable
    {
        private readonly TestPayjoinPluginDbContextFactory _dbContextFactory;

        public TestContext()
        {
            _dbContextFactory = new TestPayjoinPluginDbContextFactory();
        }

        public PayjoinReceiverSessionStore CreateStore() => new(_dbContextFactory);

        public PayjoinPluginDbContext CreateDbContext() => _dbContextFactory.CreateContext();

        public void Dispose()
        {
            using var context = _dbContextFactory.CreateContext();
            context.Database.EnsureDeleted();
        }
    }

    private sealed class TestPayjoinPluginDbContextFactory : PayjoinPluginDbContextFactory
    {
        private readonly DbContextOptions<PayjoinPluginDbContext> _dbContextOptions;

        public TestPayjoinPluginDbContextFactory()
            : base(Options.Create(new DatabaseOptions
            {
                ConnectionString = "Host=localhost;Database=payjoin-plugin-tests;Username=postgres"
            }))
        {
            var databaseRoot = new InMemoryDatabaseRoot();
            var databaseName = $"payjoin-plugin-tests-{Guid.NewGuid():N}";
            _dbContextOptions = new DbContextOptionsBuilder<PayjoinPluginDbContext>()
                .UseInMemoryDatabase(databaseName, databaseRoot)
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
