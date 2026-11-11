using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;
using Payjoin;
using Xunit;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Uri = System.Uri;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests;

[Collection(nameof(BTCPayServer.Tests.NonParallelizableCollectionDefinition))]
public class PayjoinSenderDatabaseIntegrationTests
{
    [Theory, Trait("Integration", "Integration")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CoreCancellationNeverBroadcasts(bool signingRequest, bool exposed)
    {
        var database = new DatabaseFixture();
        await using var lifetime = database.ConfigureAwait(false);
        await database.InitializeAsync();
        using var events = new EventAggregator(new BTCPayServer.Logging.Logs());
        var pending = database.CreatePendingService(events);
        var request = await pending.CreatePendingTransaction("store", "BTC", PSBT.Parse(PayjoinMethods.OriginalPsbt(), Network.TestNet),
            BTCPayServer.Abstractions.RequestBaseUrl.FromUrl(new Uri("http://localhost/")), cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true);
        var store = database.CreateStore();
        store.CreateSession("cancel", "store", "bitcoin:cancel", "test", 1000, "original", ["bootstrap"],
            outpointsUsed: ["held:0"], originalTransactionHex: "signed-original",
            pendingTransactionId: signingRequest ? request.Id : null,
            coinReservationTransactionId: signingRequest ? null : request.Id,
            status: signingRequest ? PayjoinSenderSessionStatus.AwaitingSignature : PayjoinSenderSessionStatus.Pending);
        if (exposed) Assert.True(store.TryMarkPaymentExposed("cancel"));
        await pending.CancelPendingTransaction(new("BTC", "store", request.Id)).ConfigureAwait(true);
        // No network or explorer dependency: any attempt to broadcast fails this test.
        var handler = new PayjoinSenderSignatureHandler(store, pending, null!, null!, NullLogger<PayjoinSenderSignatureHandler>.Instance);
        await handler.ReconcileAsync(TestContext.Current.CancellationToken);
        Assert.True(store.TryGetSession("cancel", out var session));
        Assert.Null(session!.BroadcastTransactionId);
        Assert.Equal(exposed ? 1 : 0, store.GetOutpointsHeldByLiveSessions("store").Count);
        Assert.Equal(exposed ? (signingRequest ? PayjoinSenderSessionStatus.AwaitingSignature : PayjoinSenderSessionStatus.Pending)
            : PayjoinSenderSessionStatus.Failed, session.Status);
        if (!exposed)
        {
            Assert.Null(session.PendingTransactionId);
            Assert.Null(session.CoinReservationTransactionId);
        }
    }

    [Fact, Trait("Integration", "Integration")]
    public async Task FailedSigningHandoffRollsBackTheCoreRequestAndRetriesOnce()
    {
        var database = new DatabaseFixture();
        await using var lifetime = database.ConfigureAwait(false);
        await database.InitializeAsync();
        using var events = new EventAggregator(new BTCPayServer.Logging.Logs());
        var store = database.CreateStore();
        var session = CreateSession(store, "handoff", ["bootstrap"]);
        var service = new PayjoinSenderSigningRequestService(database.Networks, database.CoreFactory, events, NullLogger<PendingTransactionService>.Instance);
        var published = new List<PendingTransactionService.PendingTransactionEvent>();
        using var subscription = events.Subscribe<PendingTransactionService.PendingTransactionEvent>(published.Add);
        Task<string?> CreateRequest() => service.CreateAsync(store, session,
            PSBT.Parse(PayjoinMethods.OriginalPsbt(), Network.TestNet),
            BTCPayServer.Abstractions.RequestBaseUrl.FromUrl(new Uri("http://localhost/")), TestContext.Current.CancellationToken);
        database.Factory.BeforeSave = () => throw new DbUpdateException("Injected handoff failure");
        var failure = await Assert.ThrowsAsync<DbUpdateException>(CreateRequest);
        Assert.Equal("Injected handoff failure", failure.Message);
        database.Factory.BeforeSave = null;
        using (var core = database.CoreFactory.CreateContext())
            Assert.Empty(core.PendingTransactions.ToArray()); // Not even an orphaned Cancelled row.
        Assert.True(store.TryGetSession("handoff", out var unchanged));
        Assert.Equal(PayjoinSenderSessionStatus.Pending, unchanged!.Status);
        Assert.Null(unchanged.PendingTransactionId);
        Assert.Empty(published);

        var id = await CreateRequest();
        Assert.NotNull(id);
        Assert.Null(await CreateRequest());
        Assert.Equal(id, Assert.Single(published).Data.Id);
        using (var core = database.CoreFactory.CreateContext())
            Assert.Equal(id, Assert.Single(core.PendingTransactions.ToArray()).Id);
        // Fresh context/store sees the committed handoff after a process would restart.
        Assert.True(database.CreateStore().TryGetSession("handoff", out var linked));
        Assert.Equal(id, linked!.PendingTransactionId);
        Assert.Equal(PayjoinSenderSessionStatus.AwaitingSignature, linked.Status);
    }

    [Theory, Trait("Integration", "Integration")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverlappingTransitionsCannotOverwriteTheWinningCompletion(bool competingCompletion)
    {
        var database = new DatabaseFixture();
        await using var lifetime = database.ConfigureAwait(false);
        using (var context = database.Factory.CreateContext())
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        var store = database.CreateStore();
        CreateSession(store, "race", ["bootstrap"]);
        database.Factory.BeforeSave = () =>
        {
            database.Factory.BeforeSave = null;
            Assert.True(store.CompleteSession("race", PayjoinSenderSessionStatus.CompletedFallback, "winner", null));
        };
        Assert.False(competingCompletion
            ? store.CompleteSession("race", PayjoinSenderSessionStatus.CompletedPayjoin, "loser", null)
            : store.AwaitSignature("race", "late"));
        Assert.True(store.TryGetSession("race", out var current));
        Assert.Equal(PayjoinSenderSessionStatus.CompletedFallback, current!.Status);
        Assert.Equal("winner", current.BroadcastTransactionId);
        Assert.Null(current.PendingTransactionId);
    }

    [Fact, Trait("Integration", "Integration")]
    public async Task OwnershipSaturationDefersAndReleasesCapacity()
    {
        var database = new DatabaseFixture();
        await using var lifetime = database.ConfigureAwait(false);
        using (var context = database.Factory.CreateContext())
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        var store = database.CreateStore();
        var leases = new List<IAsyncDisposable>();
        try
        {
            for (var i = 0; i < PayjoinSenderSessionLock.Capacity; i++)
            {
                var lease = await store.TryLockSessionAsync($"held-{i}", TestContext.Current.CancellationToken);
                Assert.NotNull(lease);
                leases.Add(lease);
            }
            Assert.Null(await store.TryLockSessionAsync("excess", TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken).ConfigureAwait(true));
            await leases[0].DisposeAsync().ConfigureAwait(true);
            leases.RemoveAt(0);
            var resumed = await store.TryLockSessionAsync("excess", TestContext.Current.CancellationToken);
            Assert.NotNull(resumed);
            leases.Add(resumed);
        }
        finally
        {
            foreach (var lease in leases) await lease.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact, Trait("Integration", "Integration")]
    public async Task MoreThanOnePoolOfSessionsProgressWithBoundedLongPolls()
    {
        var database = new DatabaseFixture();
        await using var lifetime = database.ConfigureAwait(false);
        using (var context = database.Factory.CreateContext())
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        var store = database.CreateStore();
        using var keys = OhttpKeys.Decode(Convert.FromHexString("01001604ba48c49c3d4a92a3ad00ecc63a024da10ced02180c73ec12d8a7ad2cc91bb483824fe2bee8d28bfe2eb2fc6453bc4d31cd851e8a6540e86c5382af588d370957000400010003"));
        using var receiverBuilder = new ReceiverBuilder("2MuyMrZHkbHbfjudmKUy45dU4P17pjG2szK", "https://example.com", keys);
        using var receiverTransition = receiverBuilder.Build();
        using var receiver = receiverTransition.Save(new CapturingReceiverSessionPersister());
        using var uri = receiver.PjUri();
        var bootstrap = new CapturingSenderSessionPersister();
        using var builder = new SenderBuilder(PayjoinMethods.OriginalPsbt(), uri);
        using var transition = builder.BuildRecommended(1000);
        using var sender = transition.Save(bootstrap);
        for (var i = 0; i < 24; i++) CreateSession(store, $"poll-{i}", bootstrap.Load());
        var relay = new BoundedRelay();
        var handler = new PayjoinSenderSignatureHandler(store, null!, null!, null!, NullLogger<PayjoinSenderSignatureHandler>.Instance);
        var processor = new PayjoinSenderSessionProcessor(store, relay, database.Networks, null!, null!, null!, null!, handler,
            NullLogger<PayjoinSenderSessionProcessor>.Instance, null!);
        var tick = processor.ProcessTickAsync(TestContext.Current.CancellationToken);
        await relay.Full.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken).ConfigureAwait(true);
        try
        {
            Assert.Equal(8, relay.Started);
            Assert.False(tick.IsCompleted);
            CreateSession(store, "interactive", bootstrap.Load());
            Assert.True((await processor.CancelAsync("store", "interactive", TestContext.Current.CancellationToken)).Success);
        }
        finally { relay.Release.TrySetResult(); }
        await tick.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(24, relay.Started);
        Assert.Equal(24, store.GetPendingSessions().Count);
    }

    private sealed class BoundedRelay : IPayjoinReceiverRelayRequestSender
    {
        private int _started;
        public int Started => Volatile.Read(ref _started);
        public TaskCompletionSource Full { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<(byte[] ResponseBody, TRequestContext RequestContext)> SendAsync<TRequestContext>(
            string storeId, string sessionId, Func<string, TRequestContext> buildRequest,
            Func<TRequestContext, (Uri Url, string ContentType, byte[] Body)> describeRequest,
            CancellationToken cancellationToken) where TRequestContext : IDisposable
        {
            if (Interlocked.Increment(ref _started) == 8) Full.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException("Simulated lost relay response.");
        }
    }

    [Fact, Trait("Integration", "Integration")]
    public async Task DispatchMigrationPreservesUncertaintyForExistingSignedSessions()
    {
        var database = new DatabaseFixture();
        await using var databaseLifetime = database.ConfigureAwait(false);
        var context = database.Factory.CreateContext();
        await using var contextLifetime = context.ConfigureAwait(false);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20260821075336_AddSenderSessions", TestContext.Current.CancellationToken).ConfigureAwait(true);
        await context.Database.ExecuteSqlRawAsync("""
            INSERT INTO "BTCPayServer.Plugins.Payjoin"."SenderSessions"
                ("SenderSessionId", "StoreId", "Bip21", "DestinationAddress", "AmountSats", "OriginalTransactionId",
                 "FeeRateSatPerKwu", "OutpointsUsed", "OriginalTransactionHex", "Status", "CreatedAt", "UpdatedAt")
            VALUES ('signed', 'store', 'bitcoin:signed', 'test', 1000, 'original-signed', 1000, ARRAY[]::text[], 'signed-hex', 0, NOW(), NOW()),
                   ('unsigned', 'store', 'bitcoin:unsigned', 'test', 1000, 'original-unsigned', 1000, ARRAY[]::text[], NULL, 4, NOW(), NOW()),
                   ('events', 'store', 'bitcoin:events', 'test', 1000, 'original-events', 1000, ARRAY[]::text[], NULL, 0, NOW(), NOW());
            INSERT INTO "BTCPayServer.Plugins.Payjoin"."SenderSessionEvents" ("SenderSessionId", "Sequence", "Event", "CreatedAt")
            VALUES ('events', 1, 'legacy-event', NOW());
            """, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await migrator.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true);
        var store = database.CreateStore();
        Assert.True(store.TryGetSession("signed", out var signed));
        Assert.True(signed!.PaymentExposed);
        Assert.True(store.TryGetSession("unsigned", out var unsigned));
        Assert.False(unsigned!.PaymentExposed);
        Assert.True(store.TryGetSession("events", out var events));
        Assert.True(events!.PaymentExposed);
        Assert.False(CreateSession(store, "new").PaymentExposed);
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact, Trait("Integration", "Integration")]
    public async Task SessionOwnershipExcludesAnIndependentDatabaseConnection()
    {
        var database = new DatabaseFixture();
        await using var databaseLifetime = database.ConfigureAwait(false);
        var context = database.Factory.CreateContext();
        await using var contextLifetime = context.ConfigureAwait(false);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        var store = database.CreateStore();
        var lease = await store.TryLockSessionAsync("owned", TestContext.Current.CancellationToken);
        Assert.NotNull(lease);
        await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        try
        {
            Assert.False(await TryLockAsync(context, "owned"));
            Assert.True(await TryLockAsync(context, "independent"));
        }
        finally { await lease.DisposeAsync().ConfigureAwait(true); }
        Assert.True(await TryLockAsync(context, "owned"));
    }

    [Fact, Trait("Integration", "Integration")]
    public async Task CompetingRealFfiTransitionsHaveOneWinnerAndReplayOnPostgres()
    {
        var database = new DatabaseFixture();
        await using var databaseLifetime = database.ConfigureAwait(false);
        var context = database.Factory.CreateContext();
        await using var contextLifetime = context.ConfigureAwait(false);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        var store = database.CreateStore();
        using var keys = OhttpKeys.Decode(Convert.FromHexString("01001604ba48c49c3d4a92a3ad00ecc63a024da10ced02180c73ec12d8a7ad2cc91bb483824fe2bee8d28bfe2eb2fc6453bc4d31cd851e8a6540e86c5382af588d370957000400010003"));
        using var receiverBuilder = new ReceiverBuilder("2MuyMrZHkbHbfjudmKUy45dU4P17pjG2szK", "https://example.com", keys);
        using var receiverTransition = receiverBuilder.Build();
        using var receiver = receiverTransition.Save(new CapturingReceiverSessionPersister());
        using var uri = receiver.PjUri();
        var bootstrap = new CapturingSenderSessionPersister();
        using var builder = new SenderBuilder(PayjoinMethods.OriginalPsbt(), uri);
        using var transition = builder.BuildRecommended(1000);
        using var sender = transition.Save(bootstrap);
        CreateSession(store, "race", bootstrap.Load());
        var first = store.CreatePersister("race");
        var second = store.CreatePersister("race");
        using var firstReplay = PayjoinMethods.ReplaySenderEventLog(first);
        using var secondReplay = PayjoinMethods.ReplaySenderEventLog(second);
        using var firstState = firstReplay.State();
        using var secondState = secondReplay.State();
        using var a = Assert.IsType<SendSession.WithReplyKey>(firstState).Inner.Cancel();
        using var b = Assert.IsType<SendSession.WithReplyKey>(secondState).Inner.Cancel();
        bool Save(SenderCancelTransition candidate, JsonSenderSessionPersister persister)
        {
            try { using var saved = candidate.Save(persister); return true; }
            catch (SenderPersistedException.Storage) { return false; }
        }
        var outcomes = await Task.WhenAll(Task.Run(() => Save(a, first), TestContext.Current.CancellationToken),
            Task.Run(() => Save(b, second), TestContext.Current.CancellationToken)).ConfigureAwait(true);
        Assert.Single(outcomes, won => won);
        using var replay = PayjoinMethods.ReplaySenderEventLog(store.CreatePersister("race"));
        using var state = replay.State();
        Assert.IsType<SendSession.SenderPendingFallback>(state);
    }

    private static async Task<bool> TryLockAsync(PayjoinPluginDbContext context, string id)
    {
        using var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = context.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = "SELECT pg_try_advisory_xact_lock(hashtextextended(@key, 0))";
        command.Parameters.Add(new NpgsqlParameter("key", $"payjoin-sender:{id}"));
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken).ConfigureAwait(false) is true;
    }

    private static PayjoinSenderSessionState CreateSession(PayjoinSenderSessionStore store, string id, string[]? events = null)
        => store.CreateSession(id, "store", $"bitcoin:{id}", "test", 1000, id, events ?? [],
            status: events is null ? PayjoinSenderSessionStatus.AwaitingSignature : PayjoinSenderSessionStatus.Pending);

    private sealed class DatabaseFixture : IAsyncDisposable
    {
        public InterceptingFactory Factory { get; }
        public ApplicationDbContextFactory CoreFactory { get; }
        public BTCPayNetworkProvider Networks { get; }
        public DatabaseFixture()
        {
            var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TESTS_POSTGRES") ??
                "Host=127.0.0.1;Port=39372;Username=postgres") { Database = $"btcpayserver_pr113_{Guid.NewGuid():N}" };
            var options = Options.Create(new DatabaseOptions { ConnectionString = connection.ConnectionString });
            Factory = new InterceptingFactory(options);
            CoreFactory = new ApplicationDbContextFactory(options, NullLoggerFactory.Instance);
            var nbx = new NBXplorer.NBXplorerNetworkProvider(ChainName.Testnet);
            var network = new BTCPayNetwork { CryptoCode = "BTC", NBXplorerNetwork = nbx.GetFromCryptoCode("BTC"), DefaultSettings = new BTCPayDefaultSettings() };
            Networks = new BTCPayNetworkProvider([network], nbx, new BTCPayServer.Logging.Logs());
        }
        public async Task InitializeAsync()
        {
            using var core = CoreFactory.CreateContext();
            await core.Database.MigrateAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
            core.Stores.Add(new StoreData { Id = "store", StoreName = "Review fixture" });
            await core.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
            using var plugin = Factory.CreateContext();
            await plugin.Database.MigrateAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
        public PendingTransactionService CreatePendingService(EventAggregator events)
        {
            return new PendingTransactionService(Networks,
                CoreFactory, events, NullLogger<PendingTransactionService>.Instance);
        }
        public PayjoinSenderSessionStore CreateStore() => new(Factory, new PostgresPayjoinUniqueConstraintViolationDetector());
        public async ValueTask DisposeAsync()
        {
            var context = Factory.CreateContext();
            await using var contextLifetime = context.ConfigureAwait(false);
            await context.Database.EnsureDeletedAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private sealed class InterceptingFactory(IOptions<DatabaseOptions> options) : PayjoinPluginDbContextFactory(options)
    {
        public Action? BeforeSave { get; set; }
        public override PayjoinPluginDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptionsAction = null)
        {
            var builder = new DbContextOptionsBuilder<PayjoinPluginDbContext>();
            ConfigureBuilder(builder, npgsqlOptionsAction);
            builder.AddInterceptors(new SaveInterceptor(this));
            return new PayjoinPluginDbContext(builder.Options);
        }
    }

    private sealed class SaveInterceptor(InterceptingFactory factory) : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> SavingChanges(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result)
        {
            factory.BeforeSave?.Invoke();
            return result;
        }
    }
}
