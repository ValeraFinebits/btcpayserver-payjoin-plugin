using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using NBitcoin;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class PayjoinReceiverSessionStoreTests
{
    [Fact]
    public void CreateSessionRoundTripsThroughFreshStoreInstance()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();

        var createdSession = CreateSession(store, "invoice-create");

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
        var firstSession = CreateSession(firstStore, "invoice-events");

        var firstPersister = firstStore.CreatePersister(firstSession);
        firstPersister.Save("event-1");

        var secondStore = testContext.CreateStore();
        Assert.True(secondStore.TryGetSession(firstSession.InvoiceId, out var secondSession));
        var secondPersister = secondStore.CreatePersister(secondSession!);

        Assert.Equal(new[] { "bootstrap-event", "event-1" }, secondPersister.Load());

        secondPersister.Save("event-2");

        var thirdStore = testContext.CreateStore();
        Assert.True(thirdStore.TryGetSession(firstSession.InvoiceId, out var thirdSession));
        var thirdPersister = thirdStore.CreatePersister(thirdSession!);

        Assert.Equal(new[] { "bootstrap-event", "event-1", "event-2" }, thirdPersister.Load());

        using var context = testContext.CreateDbContext();
        var persistedSequences = context.ReceiverSessionEvents
            .OrderBy(x => x.Sequence)
            .Select(x => x.Sequence)
            .ToArray();
        Assert.Equal(new[] { 1, 2, 3 }, persistedSequences);
    }

    [Fact]
    public void ReservedContributedInputRoundTripsThroughFreshStoreInstance()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();
        var session = CreateSession(store, "invoice-input");
        var expectedOutPoint = new OutPoint(uint256.Parse("1111111111111111111111111111111111111111111111111111111111111111"), uint.MaxValue);

        Assert.True(store.TryReserveContributedInput(session.StoreId, session.InvoiceId, expectedOutPoint, DateTimeOffset.UtcNow.AddMinutes(10)));

        var reloadedStore = testContext.CreateStore();
        Assert.True(reloadedStore.TryGetSession(session.InvoiceId, out var reloadedSession));
        Assert.True(reloadedSession!.TryGetContributedInput(out var actualOutPoint));
        Assert.Equal(expectedOutPoint, actualOutPoint);
    }

    [Fact]
    public void ReservingSameContributedInputDoesNotAdvanceUpdatedAt()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();
        var session = CreateSession(store, "invoice-same-input");
        var expectedOutPoint = new OutPoint(uint256.Parse("3333333333333333333333333333333333333333333333333333333333333333"), 3);

        Assert.True(store.TryReserveContributedInput(session.StoreId, session.InvoiceId, expectedOutPoint, DateTimeOffset.UtcNow.AddMinutes(10)));
        using var firstContext = testContext.CreateDbContext();
        var firstUpdatedAt = firstContext.ReceiverSessions
            .Single(x => x.InvoiceId == session.InvoiceId)
            .UpdatedAt;

        Assert.True(store.TryReserveContributedInput(session.StoreId, session.InvoiceId, expectedOutPoint, DateTimeOffset.UtcNow.AddMinutes(10)));
        using var secondContext = testContext.CreateDbContext();
        var secondUpdatedAt = secondContext.ReceiverSessions
            .Single(x => x.InvoiceId == session.InvoiceId)
            .UpdatedAt;

        Assert.Equal(firstUpdatedAt, secondUpdatedAt);
    }

    [Fact]
    public void ReservedContributedInputAndEventsReplayTogetherThroughFreshStoreInstance()
    {
        using var testContext = new TestContext();
        var firstStore = testContext.CreateStore();
        var firstSession = CreateSession(firstStore, "invoice-input-events");
        var expectedOutPoint = new OutPoint(uint256.Parse("2222222222222222222222222222222222222222222222222222222222222222"), 2);
        var firstPersister = firstStore.CreatePersister(firstSession);

        firstPersister.Save("event-before-input");
        Assert.True(firstStore.TryReserveContributedInput(firstSession.StoreId, firstSession.InvoiceId, expectedOutPoint, DateTimeOffset.UtcNow.AddMinutes(10)));
        firstPersister.Save("event-after-input");

        var replayedStore = testContext.CreateStore();
        Assert.True(replayedStore.TryGetSession(firstSession.InvoiceId, out var replayedSession));
        Assert.True(replayedSession!.TryGetContributedInput(out var actualOutPoint));
        Assert.Equal(expectedOutPoint, actualOutPoint);

        var replayedPersister = replayedStore.CreatePersister(replayedSession);
        Assert.Equal(new[] { "bootstrap-event", "event-before-input", "event-after-input" }, replayedPersister.Load());
    }

    [Fact]
    public void TryReserveContributedInputPersistsReservationAndContributedInput()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();
        var session = CreateSession(store, "invoice-reserve");
        var outPoint = new OutPoint(uint256.Parse("4444444444444444444444444444444444444444444444444444444444444444"), 4);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);

        Assert.True(store.TryReserveContributedInput(session.StoreId, session.InvoiceId, outPoint, expiresAt));

        var reloadedStore = testContext.CreateStore();
        Assert.True(reloadedStore.TryGetSession(session.InvoiceId, out var reloadedSession));
        Assert.True(reloadedSession!.TryGetContributedInput(out var actualOutPoint));
        Assert.Equal(outPoint, actualOutPoint);

        using var context = testContext.CreateDbContext();
        var reservation = context.ReceiverInputReservations.Single();
        Assert.Equal(session.InvoiceId, reservation.InvoiceId);
        Assert.Equal(session.StoreId, reservation.StoreId);
        Assert.Equal(outPoint.Hash.ToString(), reservation.TransactionId);
        Assert.Equal((long)outPoint.N, reservation.OutputIndex);
        Assert.Equal(expiresAt, reservation.ExpiresAt);
    }

    [Fact]
    public void TryReserveContributedInputRejectsReservationConflictForDifferentInvoice()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();
        var firstSession = CreateSession(store, "invoice-reserve-first");
        var secondSession = CreateSession(store, "invoice-reserve-second");
        var outPoint = new OutPoint(uint256.Parse("5555555555555555555555555555555555555555555555555555555555555555"), 5);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);

        Assert.True(store.TryReserveContributedInput(firstSession.StoreId, firstSession.InvoiceId, outPoint, expiresAt));
        Assert.False(store.TryReserveContributedInput(secondSession.StoreId, secondSession.InvoiceId, outPoint, expiresAt));
    }

    [Fact]
    public void TryReserveContributedInputIsIdempotentForSameInvoiceAndOutPoint()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();
        var session = CreateSession(store, "invoice-reserve-idempotent");
        var outPoint = new OutPoint(uint256.Parse("6666666666666666666666666666666666666666666666666666666666666666"), 6);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);

        Assert.True(store.TryReserveContributedInput(session.StoreId, session.InvoiceId, outPoint, expiresAt));
        Assert.True(store.TryReserveContributedInput(session.StoreId, session.InvoiceId, outPoint, expiresAt));

        using var context = testContext.CreateDbContext();
        Assert.Single(context.ReceiverInputReservations);
    }

    [Fact]
    public void TryReserveContributedInputRejectsChangingExistingContributedInput()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();
        var session = CreateSession(store, "invoice-reserve-change");
        var firstOutPoint = new OutPoint(uint256.Parse("7777777777777777777777777777777777777777777777777777777777777777"), 7);
        var secondOutPoint = new OutPoint(uint256.Parse("8888888888888888888888888888888888888888888888888888888888888888"), 8);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);

        Assert.True(store.TryReserveContributedInput(session.StoreId, session.InvoiceId, firstOutPoint, expiresAt));
        Assert.False(store.TryReserveContributedInput(session.StoreId, session.InvoiceId, secondOutPoint, expiresAt));
    }

    [Fact]
    public void RemoveSessionDeletesSessionAndEvents()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();
        var session = CreateSession(store, "invoice-remove");
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
    public void RemoveSessionDeletesInputReservations()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();
        var session = CreateSession(store, "invoice-remove-reservation");
        var outPoint = new OutPoint(uint256.Parse("9999999999999999999999999999999999999999999999999999999999999999"), 9);

        Assert.True(store.TryReserveContributedInput(session.StoreId, session.InvoiceId, outPoint, DateTimeOffset.UtcNow.AddMinutes(10)));
        Assert.True(store.RemoveSession(session.InvoiceId));

        using var context = testContext.CreateDbContext();
        Assert.Empty(context.ReceiverInputReservations);
    }

    [Fact]
    public void CleanupExpiredInputReservationsRemovesOnlyExpiredReservations()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();
        var expiredSession = CreateSession(store, "invoice-expired-reservation");
        var activeSession = CreateSession(store, "invoice-active-reservation");

        Assert.True(store.TryReserveContributedInput(
            expiredSession.StoreId,
            expiredSession.InvoiceId,
            new OutPoint(uint256.Parse("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), 1),
            DateTimeOffset.UtcNow.AddMinutes(-1)));
        Assert.True(store.TryReserveContributedInput(
            activeSession.StoreId,
            activeSession.InvoiceId,
            new OutPoint(uint256.Parse("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), 2),
            DateTimeOffset.UtcNow.AddMinutes(10)));

        var affected = store.CleanupExpiredInputReservations(DateTimeOffset.UtcNow);

        Assert.Equal(1, affected);
        using var context = testContext.CreateDbContext();
        var remainingReservation = Assert.Single(context.ReceiverInputReservations);
        Assert.Equal(activeSession.InvoiceId, remainingReservation.InvoiceId);
    }

    [Fact]
    public void CleanupExpiredInputReservationsClearsMatchingSessionMetadata()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();
        var session = CreateSession(store, "invoice-expired-metadata");
        var reservedOutPoint = new OutPoint(uint256.Parse("cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"), 3);

        Assert.True(store.TryReserveContributedInput(
            session.StoreId,
            session.InvoiceId,
            reservedOutPoint,
            DateTimeOffset.UtcNow.AddMinutes(-1)));

        store.CleanupExpiredInputReservations(DateTimeOffset.UtcNow);

        var reloadedStore = testContext.CreateStore();
        Assert.True(reloadedStore.TryGetSession(session.InvoiceId, out var reloadedSession));
        Assert.False(reloadedSession!.TryGetContributedInput(out _));
    }

    [Fact]
    public void CloseRequestedInitializedPollConsumptionPersistsThroughFreshStoreInstance()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();
        var session = CreateSession(store, "invoice-close");

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

    [Fact]
    public void CreateSessionWithBootstrapEventsPersistsThemAtomically()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();

        var session = store.CreateSession(
            "invoice-bootstrap",
            "bcrt1qexampleaddress0000000000000000000000000",
            "store-1",
            new Uri("https://relay.example/"),
            DateTimeOffset.UtcNow.AddMinutes(15),
            new[] { "event-1", "event-2" });

        Assert.Equal(new[] { "event-1", "event-2" }, session.GetEvents());

        var reloadedStore = testContext.CreateStore();
        Assert.True(reloadedStore.TryGetSession(session.InvoiceId, out var reloadedSession));
        Assert.Equal(new[] { "event-1", "event-2" }, reloadedSession!.GetEvents());
    }

    [Fact]
    public void CreateSessionRejectsEmptyBootstrapEvents()
    {
        using var testContext = new TestContext();
        var store = testContext.CreateStore();

        var exception = Assert.Throws<ArgumentException>(() => store.CreateSession(
            "invoice-empty-bootstrap",
            "bcrt1qexampleaddress0000000000000000000000000",
            "store-1",
            new Uri("https://relay.example/"),
            DateTimeOffset.UtcNow.AddMinutes(15),
            []));

        Assert.Equal("bootstrapEvents", exception.ParamName);
    }

    private static PayjoinReceiverSessionState CreateSession(PayjoinReceiverSessionStore store, string invoiceId)
    {
        return store.CreateSession(
            invoiceId,
            "bcrt1qexampleaddress0000000000000000000000000",
            "store-1",
            new Uri("https://relay.example/"),
            DateTimeOffset.UtcNow.AddMinutes(15),
            ["bootstrap-event"]);
    }

    private sealed class TestContext : IDisposable
    {
        private readonly TestPayjoinPluginDbContextFactory _dbContextFactory;
        private readonly PostgresPayjoinUniqueConstraintViolationDetector _uniqueConstraintViolationDetector;

        public TestContext()
        {
            _dbContextFactory = new TestPayjoinPluginDbContextFactory();
            _uniqueConstraintViolationDetector = new PostgresPayjoinUniqueConstraintViolationDetector();
        }

        public PayjoinReceiverSessionStore CreateStore() => new(_dbContextFactory, _uniqueConstraintViolationDetector);

        public PayjoinPluginDbContext CreateDbContext() => _dbContextFactory.CreateContext();

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
