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

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests;

public class PayjoinSenderDatabaseIntegrationTests
{
    [Fact, Trait("Integration", "Integration")]
    public async Task Rc2UpgradePreservesReceiverStateAndSenderExposure()
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
            INSERT INTO "BTCPayServer.Plugins.Payjoin"."ReceiverSessions"
                ("InvoiceId", "StoreId", "ReceiverAddress", "MonitoringExpiresAt", "CreatedAt", "UpdatedAt",
                 "IsCloseRequested", "InitializedPollAfterCloseRequestConsumed", "PayjoinUri", "DestructiveWriteStamp")
            VALUES ('receiver-a', 'store', 'legacy-address-a', NOW() + INTERVAL '1 hour', NOW(), NOW(),
                    FALSE, FALSE, 'bitcoin:legacy-address-a?amount=0.00001000', 7),
                   ('receiver-b', 'store', 'legacy-address-b', NOW() + INTERVAL '1 hour', NOW(), NOW(),
                    FALSE, FALSE, 'bitcoin:legacy-address-b?amount=0.00002000', 11);
            INSERT INTO "BTCPayServer.Plugins.Payjoin"."ReceiverSessionEvents" ("InvoiceId", "Sequence", "Event", "CreatedAt")
            VALUES ('receiver-a', 1, 'legacy-receiver-event', NOW());
            INSERT INTO "BTCPayServer.Plugins.Payjoin"."ReceiverInputReservations"
                ("InvoiceId", "StoreId", "TransactionId", "OutputIndex", "ReservedAt", "ExpiresAt")
            VALUES ('receiver-a', 'store', 'legacy-reserved-input', 2, NOW(), NOW() + INTERVAL '1 hour');
            INSERT INTO "BTCPayServer.Plugins.Payjoin"."AccountingBridges"
                ("InvoiceId", "StoreId", "CryptoCode", "PaymentMethodId", "EffectiveInvoiceValueSats", "Status", "CreatedAt", "UpdatedAt")
            VALUES ('receiver-a', 'store', 'BTC', 'BTC-CHAIN', 1000, 0, NOW(), NOW());
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
        var receivers = await context.ReceiverSessions.OrderBy(session => session.InvoiceId)
            .ToArrayAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(2, receivers.Length);
        Assert.All(receivers, session => Assert.NotEqual(Guid.Empty, session.SessionInstanceId));
        Assert.NotEqual(receivers[0].SessionInstanceId, receivers[1].SessionInstanceId);
        Assert.Equal(7, receivers[0].AttemptVersion);
        Assert.Equal(11, receivers[1].AttemptVersion);
        Assert.Equal("bitcoin:legacy-address-a?amount=0.00001000", receivers[0].PayjoinUri);
        Assert.Equal("legacy-receiver-event", (await context.ReceiverSessionEvents.SingleAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)).Event);
        var reservation = await context.ReceiverInputReservations.SingleAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal("legacy-reserved-input", reservation.TransactionId);
        Assert.Equal(2, reservation.OutputIndex);
        var bridge = await context.AccountingBridges.SingleAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(1000, bridge.EffectiveInvoiceValueSats);
        Assert.Null(bridge.SettlementKeyPath);
        Assert.Empty(await context.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
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
        public PayjoinPluginDbContextFactory Factory { get; }
        public DatabaseFixture()
        {
            var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TESTS_POSTGRES") ??
                "Host=127.0.0.1;Port=39372;Username=postgres") { Database = $"btcpayserver_pr113_{Guid.NewGuid():N}" };
            Factory = new PayjoinPluginDbContextFactory(Options.Create(new DatabaseOptions { ConnectionString = connection.ConnectionString }));
        }
        public PayjoinSenderSessionStore CreateStore() => new(Factory, new PostgresPayjoinUniqueConstraintViolationDetector());
        public async ValueTask DisposeAsync()
        {
            var context = Factory.CreateContext();
            await using var contextLifetime = context.ConfigureAwait(false);
            await context.Database.EnsureDeletedAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
