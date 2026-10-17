using System.Collections.Concurrent;
using System.Data.Common;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Logging;
using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBXplorer;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Payjoin;
using Xunit;
using OutPoint = NBitcoin.OutPoint;
using Uri = System.Uri;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests;

[Collection(typeof(NonParallelizableCollectionDefinition))]
public partial class PayjoinHistoryRetentionIntegrationTests(ITestOutputHelper helper) : UnitTestBase(helper)
{
    private const string InvoiceId = "history-invoice";
    private const string ReceiverAddress = "receiver";
    private const string SessionUri = "bitcoin:receiver?amount=0.0005&pj=https%3A%2F%2Fexample.com%2Foriginal%2FURI&pjos=0";
    private static readonly string[] OriginalEvents = ["{\"original\":\"unaltered\"}", "{\"outputs\":\"committed\"}"];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PausedHistorySkipsEventQueriesButRetainsDiagnosticsAndReservationCleanup(bool failed)
    {
        var token = TestContext.Current.CancellationToken;
        var database = CreateDBTester();
        await database.MigrateAsync().WaitAsync(token).ConfigureAwait(true);
        try
        {
            var factory = new ObservedFactory(database.ConnectionString);
            await InitializeAsync(factory, true, token).ConfigureAwait(true);
            var store = CreateStore(factory);
            var bridge = CreateBridgeService(factory);
            await CreateFinalizer(store, bridge, new StopBeforeNetwork()).EnsureExpectedFinalTransactionAsync(
                FinalizationContext(store), CreateProposal(out _), token).ConfigureAwait(true);
            if (failed) await bridge.MarkFailedAsync(InvoiceId, "manual recovery", token).ConfigureAwait(true);
            else await bridge.ExpirePendingAsync(DateTimeOffset.UtcNow.AddDays(1), token).ConfigureAwait(true);
            factory.Commands.Clear();
            Assert.Empty(store.GetSessions());
            Assert.DoesNotContain(factory.Commands, sql => sql.Contains("ReceiverSessionEvents", StringComparison.Ordinal));
            Assert.True(store.TryGetSession(InvoiceId, out var held));
            Assert.NotNull(held);
            Assert.Equal(SessionUri, held.PayjoinUri);
            Assert.Equal(OriginalEvents, held.GetEvents());
            Assert.Equal(1, store.CleanupExpiredInputReservations(DateTimeOffset.UtcNow.AddDays(2)));
            Assert.Equal(held.GetEvents(), store.GetAttempt(InvoiceId, held.SessionInstanceId).GetEvents());
            Assert.Equal(held.PayjoinUri, store.GetAttempt(InvoiceId, held.SessionInstanceId).PayjoinUri);
            Assert.False(store.RemoveSession(InvoiceId));
            var retry = await bridge.TryRetryAsync(InvoiceId, "store", DateTimeOffset.UtcNow, token).ConfigureAwait(true);
            if (failed) { Assert.Null(retry); return; }
            Assert.NotNull(retry);
            Assert.Contains(await bridge.GetPendingReconciliationAsync(DateTimeOffset.UtcNow, token).ConfigureAwait(true), item => item.InvoiceId == InvoiceId);
            Assert.Single(store.GetSessions());
            await bridge.MarkReconciledAsync(InvoiceId, null, null, null, DateTimeOffset.UtcNow, token).ConfigureAwait(true);
            Assert.True(store.TryRemoveSessionUnlessNegotiating(InvoiceId));
            Assert.False(store.TryGetSession(InvoiceId, out _));
        }
        finally
        {
            await using var core = database.CreateContext();
            await core.Database.EnsureDeletedAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    public static IEnumerable<object[]> DeletionRaces()
    {
        foreach (var conditional in new[] { false, true })
            foreach (var transition in new[] { "failed", "finalize", "backfill" })
                foreach (var point in new[] { "before", "session-read", "bridge-checked" })
                    yield return [conditional, transition, point];
    }

    [Theory]
    [MemberData(nameof(DeletionRaces))]
    [Trait("Integration", "Integration")]
    public async Task ProtectingCommitPreventsDeletionAndRollsBackPartialDeletes(bool conditional, string transition, string point)
    {
        var token = TestContext.Current.CancellationToken;
        var database = CreateDBTester();
        await database.MigrateAsync().WaitAsync(token).ConfigureAwait(true);
        try
        {
            using var barrier = new WriteBarrier(point);
            var writerFactory = new ObservedFactory(database.ConnectionString);
            var deleteFactory = new ObservedFactory(database.ConnectionString, barrier);
            var store = CreateStore(writerFactory);
            var deletingStore = CreateStore(deleteFactory);
            var bridge = CreateBridgeService(writerFactory);
            var relay = new StopBeforeNetwork();
            var proposal = CreateProposal(out var expectedId);
            var proposalEvent = "{\"signedProposal\":\"" + proposal.Psbt() + "\"}";
            await InitializeAsync(writerFactory, true, token,
                transition == "finalize" ? OriginalEvents : [.. OriginalEvents, proposalEvent]).ConfigureAwait(true);

            async Task ProtectAsync()
            {
                switch (transition)
                {
                    case "failed":
                        await bridge.MarkFailedAsync(InvoiceId, "manual recovery evidence", token).ConfigureAwait(true);
                        break;
                    case "backfill":
                        await CreateFinalizer(store, bridge, relay).EnsureExpectedFinalTransactionAsync(
                            FinalizationContext(store), proposal, token).ConfigureAwait(true);
                        break;
                    default:
                        var finalizer = CreateFinalizer(store, bridge, relay);
                        await Assert.ThrowsAsync<NetworkStoppedException>(() => finalizer.FinalizeCoreAsync(
                            FinalizationContext(store), capture => { capture.Save(proposalEvent); return proposal; }, token)).ConfigureAwait(true);
                        break;
                }
            }

            bool Delete() => conditional ? deletingStore.TryRemoveSessionUnlessNegotiating(InvoiceId) : deletingStore.RemoveSession(InvoiceId);
            if (point == "before")
            {
                await ProtectAsync().ConfigureAwait(true);
                Assert.False(Delete());
            }
            else
            {
                var deletion = Task.Run(Delete, token);
                try
                {
                    await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20), token).ConfigureAwait(true);
                    await ProtectAsync().ConfigureAwait(true);
                }
                finally { barrier.Release.TrySetResult(); }
                Assert.False(await deletion.WaitAsync(token).ConfigureAwait(true));
            }

            Assert.True(store.TryGetSession(InvoiceId, out var retained));
            Assert.NotNull(retained);
            Assert.Equal(SessionUri, retained.PayjoinUri);
            Assert.Equal([.. OriginalEvents, proposalEvent], retained.GetEvents());
            using var context = writerFactory.CreateContext();
            var saved = await context.AccountingBridges.SingleAsync(token).ConfigureAwait(true);
            Assert.Equal(transition == "failed" ? PayjoinAccountingBridgeStatus.Failed : PayjoinAccountingBridgeStatus.PendingFinalTransaction, saved.Status);
            Assert.Equal(transition == "failed" ? null : expectedId, saved.ExpectedFinalTransactionId);
            Assert.Equal(50_000, saved.EffectiveInvoiceValueSats);
            Assert.Equal("51", saved.SettlementScript);
            Assert.Equal(1, await context.ReceiverInputReservations.CountAsync(token).ConfigureAwait(true));
            Assert.Equal(1, (await context.ReceiverSessions.SingleAsync(token).ConfigureAwait(true)).AttemptVersion);
            Assert.Equal(transition == "finalize" ? 1 : 0, relay.Calls);
            Assert.Empty(writerFactory.ProcessIds.Keys.Intersect(deleteFactory.ProcessIds.Keys));
            Assert.NotEmpty(deleteFactory.ProcessIds);
            if (point == "bridge-checked")
            {
                Assert.Contains(deleteFactory.Commands, sql => sql.StartsWith("DELETE FROM", StringComparison.Ordinal) && sql.Contains("ReceiverSessionEvents", StringComparison.Ordinal));
                Assert.Contains(deleteFactory.Commands, sql => sql.StartsWith("DELETE FROM", StringComparison.Ordinal) && sql.Contains("ReceiverInputReservations", StringComparison.Ordinal));
            }
        }
        finally
        {
            await using var core = database.CreateContext();
            await core.Database.EnsureDeletedAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Integration", "Integration")]
    public async Task DeletionCommittedFirstPreventsProposalPersistenceAndPosting(bool backfill)
    {
        var token = TestContext.Current.CancellationToken;
        var database = CreateDBTester();
        await database.MigrateAsync().WaitAsync(token).ConfigureAwait(true);
        try
        {
            var factory = new ObservedFactory(database.ConnectionString);
            await InitializeAsync(factory, true, token).ConfigureAwait(true);
            using var barrier = new WriteBarrier("bridge-checked");
            var writerFactory = new ObservedFactory(database.ConnectionString, barrier);
            var store = CreateStore(writerFactory);
            var relay = new StopBeforeNetwork();
            var bridge = CreateBridgeService(writerFactory);
            var finalizer = CreateFinalizer(store, bridge, relay);
            var proposal = CreateProposal(out _);
            var writing = Task.Run(async () =>
            {
                if (backfill)
                {
                    var context = FinalizationContext(store);
                    await finalizer.EnsureExpectedFinalTransactionAsync(context, proposal, token).ConfigureAwait(false);
                    await finalizer.PostAsync(context, proposal, token).ConfigureAwait(false);
                }
                else
                {
                    await finalizer.FinalizeCoreAsync(FinalizationContext(store), capture => { capture.Save("signed proposal"); return proposal; }, token).ConfigureAwait(false);
                }
            }, token);
            try
            {
                await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20), token).ConfigureAwait(true);
                Assert.True(CreateStore(factory).RemoveSession(InvoiceId));
            }
            finally { barrier.Release.TrySetResult(); }
            if (backfill)
                await Assert.ThrowsAnyAsync<InvalidOperationException>(() => writing).ConfigureAwait(true);
            else
            {
                var exception = await Assert.ThrowsAsync<DbUpdateException>(() => writing).ConfigureAwait(true);
                Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(exception.InnerException).SqlState);
            }
            Assert.Equal(0, relay.Calls);
            using var context = factory.CreateContext();
            var saved = await context.AccountingBridges.SingleAsync(token).ConfigureAwait(true);
            Assert.Null(saved.ExpectedFinalTransactionId);
            Assert.Equal(PayjoinAccountingBridgeStatus.PendingFallback, saved.Status);
            Assert.Empty(await context.ReceiverSessions.ToArrayAsync(token).ConfigureAwait(true));
            Assert.Empty(await context.ReceiverSessionEvents.ToArrayAsync(token).ConfigureAwait(true));
            Assert.Empty(await context.ReceiverInputReservations.ToArrayAsync(token).ConfigureAwait(true));
        }
        finally
        {
            await using var core = database.CreateContext();
            await core.Database.EnsureDeletedAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    private static async Task InitializeAsync(ObservedFactory factory, bool createBridge, CancellationToken token, string[]? events = null)
    {
        using var context = factory.CreateContext();
        await context.Database.MigrateAsync(token).ConfigureAwait(false);
        var store = CreateStore(factory);
        store.GetOrCreateSession(InvoiceId, ReceiverAddress, "store", DateTimeOffset.UtcNow.AddHours(1), events ?? OriginalEvents);
        store.StorePayjoinUri(InvoiceId, ReceiverAddress, SessionUri);
        context.ReceiverInputReservations.Add(new PayjoinReceiverInputReservationData
        {
            InvoiceId = InvoiceId,
            StoreId = "store",
            TransactionId = uint256.One.ToString(),
            OutputIndex = 2,
            ReservedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        });
        await context.SaveChangesAsync(token).ConfigureAwait(false);
        if (createBridge)
            await factory.SeedBridgeAsync(BridgeRequest(), token).ConfigureAwait(false);
    }

    private static CreatePayjoinAccountingBridgeRequest BridgeRequest(string? expectedId = null) => new(
        InvoiceId, "store", "BTC", "BTC-CHAIN", DateTimeOffset.UtcNow.AddHours(1),
        FallbackTransactionId: uint256.One.ToString(), FallbackOutputIndex: 0, FallbackValueSats: 50_000,
        EffectiveInvoiceValueSats: 50_000, SettlementScript: "51", ExpectedFinalTransactionId: expectedId,
        ExpectedFinalOutputIndex: expectedId is null ? null : 0, ExpectedFinalValueSats: expectedId is null ? null : 70_000);

    private static PayjoinReceiverSessionStore CreateStore(PayjoinPluginDbContextFactory factory) => new(factory, new PostgresPayjoinUniqueConstraintViolationDetector());
    private static PayjoinAccountingBridgeService CreateBridgeService(PayjoinPluginDbContextFactory factory) => new(factory, new PayjoinSessionBuildLock());
    private static PayjoinReceiverProposalFinalizationContext FinalizationContext(PayjoinReceiverSessionStore store) =>
        new(store.CreatePersister(store.GetSessions().Single()), "store", InvoiceId, "BTC");

    private static PayjoinReceiverProposalFinalizer CreateFinalizer(PayjoinReceiverSessionStore store, PayjoinAccountingBridgeService bridge, StopBeforeNetwork relay)
    {
        var nbx = new NBXplorerNetworkProvider(ChainName.Regtest);
        var networks = new BTCPayNetworkProvider([new BTCPayNetwork { CryptoCode = "BTC", NBXplorerNetwork = nbx.GetFromCryptoCode("BTC") }], nbx, new Logs());
        return new PayjoinReceiverProposalFinalizer(relay, null!, bridge, store, networks);
    }

    private static FixedProposal CreateProposal(out string transactionId)
    {
        var transaction = Network.RegTest.CreateTransaction();
        transaction.Inputs.Add(new OutPoint(uint256.One, 2));
        transaction.Outputs.Add(Money.Satoshis(70_000), Script.FromHex("51"));
        transactionId = transaction.GetHash().ToString();
        return new FixedProposal(PSBT.FromTransaction(transaction, Network.RegTest).ToBase64());
    }

    private sealed class FixedProposal(string psbt) : IPayjoinProposal
    {
        public CancelTransition Cancel() => throw new NotSupportedException();
        public RequestResponse CreatePostRequest(string ohttpRelay) => throw new NotSupportedException();
        public PayjoinProposalTransition ProcessResponse(byte[] body, ClientResponse ohttpContext) => throw new NotSupportedException();
        public string Psbt() => psbt;
        public bool ProposalTxidIsStable() => true;
    }

    private sealed class NetworkStoppedException : Exception
    {
        public NetworkStoppedException() { }
        public NetworkStoppedException(string message) : base(message) { }
        public NetworkStoppedException(string message, Exception innerException) : base(message, innerException) { }
    }
    private sealed class StopBeforeNetwork : IPayjoinReceiverRelayRequestSender
    {
        public int Calls { get; private set; }
        public Task<(byte[] ResponseBody, TRequestContext RequestContext)> SendAsync<TRequestContext>(string storeId, string invoiceId,
            Func<string, TRequestContext> buildRequest, Func<TRequestContext, (Uri Url, string ContentType, byte[] Body)> describeRequest,
            CancellationToken cancellationToken) where TRequestContext : IDisposable
        {
            Calls++;
            throw new NetworkStoppedException();
        }
    }

    private sealed class WriteBarrier(string point) : IDisposable
    {
        private int _used;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Pause(string currentPoint)
        {
            if (point != currentPoint || Interlocked.Exchange(ref _used, 1) != 0) return;
            Entered.TrySetResult();
            Release.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        }
        public void Dispose() => Release.TrySetResult();
    }

    private sealed class ObservedFactory(string connectionString, WriteBarrier? barrier = null)
        : PayjoinPluginDbContextFactory(Options.Create(new DatabaseOptions { ConnectionString = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString }))
    {
        private WriteBarrier? Barrier => barrier;
        public bool FailNextWrite { get; set; }
        public ConcurrentDictionary<int, bool> ProcessIds { get; } = new();
        public ConcurrentQueue<string> Commands { get; } = new();
        public override PayjoinPluginDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptionsAction = null)
        {
            var builder = new DbContextOptionsBuilder<PayjoinPluginDbContext>();
            ConfigureBuilder(builder, options => { options.MaxBatchSize(1); npgsqlOptionsAction?.Invoke(options); });
            builder.AddInterceptors(new QueryObserver(this), new SaveObserver(barrier));
            return new PayjoinPluginDbContext(builder.Options);
        }

        private sealed class QueryObserver(ObservedFactory factory) : DbCommandInterceptor
        {
            private void Observe(DbCommand command)
            {
                factory.ProcessIds.TryAdd(((NpgsqlConnection)command.Connection!).ProcessID, true);
                factory.Commands.Enqueue(command.CommandText);
            }
            public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
            {
                Observe(command);
                if (factory.FailNextWrite && (command.CommandText.StartsWith("INSERT", StringComparison.Ordinal) || command.CommandText.StartsWith("UPDATE", StringComparison.Ordinal)))
                {
                    factory.FailNextWrite = false;
                    result.Dispose();
                    throw new DbUpdateException("injected interruption after the first bootstrap write");
                }
                if (command.CommandText.Contains("FROM \"BTCPayServer.Plugins.Payjoin\".\"ReceiverSessions\"", StringComparison.Ordinal))
                    factory.Barrier?.Pause("session-read");
                return result;
            }
            public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
            {
                Observe(command);
                if (command.CommandText.Contains("FROM \"BTCPayServer.Plugins.Payjoin\".\"ReceiverSessions\"", StringComparison.Ordinal))
                    factory.Barrier?.Pause("session-read");
                return ValueTask.FromResult(result);
            }
        }

        private sealed class SaveObserver(WriteBarrier? barrier) : SaveChangesInterceptor
        {
            public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
            {
                barrier?.Pause("bridge-checked");
                return result;
            }
            public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            {
                barrier?.Pause("bridge-checked");
                return ValueTask.FromResult(result);
            }
        }
    }
}

internal static class AccountingBridgeTestData
{
    internal static void SeedBridgeFields(this PayjoinPluginDbContextFactory factory, string invoiceId,
        Action<BTCPayServer.Plugins.Payjoin.Data.PayjoinAccountingBridgeData> seed)
    {
        using var context = factory.CreateContext();
        seed(context.AccountingBridges.Single(x => x.InvoiceId == invoiceId));
        var session = context.ReceiverSessions.SingleOrDefault(x => x.InvoiceId == invoiceId);
        if (session is not null) session.AttemptVersion = checked(session.AttemptVersion + 1);
        context.SaveChanges();
    }

    internal static PayjoinReceiverSessionStore.DatabaseBackedReceiverPersister Owner(this PayjoinReceiverSessionStore store, string invoiceId)
    {
        if (!store.TryGetSession(invoiceId, out var session)) throw new InvalidOperationException("Missing fixture attempt.");
        return store.CreatePersister(session!);
    }

    internal static async Task<PayjoinAccountingBridgeState> SeedBridgeAsync(this PayjoinPluginDbContextFactory factory,
        CreatePayjoinAccountingBridgeRequest request, CancellationToken token)
    {
        using var context = factory.CreateContext();
        var now = DateTimeOffset.UtcNow;
        context.AccountingBridges.Add(new BTCPayServer.Plugins.Payjoin.Data.PayjoinAccountingBridgeData
        {
            InvoiceId = request.InvoiceId,
            StoreId = request.StoreId,
            CryptoCode = request.CryptoCode,
            PaymentMethodId = request.PaymentMethodId,
            ExpiresAt = request.ExpiresAt,
            FallbackTransactionId = request.FallbackTransactionId,
            FallbackOutputIndex = request.FallbackOutputIndex,
            FallbackValueSats = request.FallbackValueSats,
            EffectiveInvoiceValueSats = request.EffectiveInvoiceValueSats,
            SettlementScript = request.SettlementScript,
            SettlementKeyPath = request.SettlementKeyPath,
            ExpectedFinalTransactionId = request.ExpectedFinalTransactionId,
            ExpectedFinalOutputIndex = request.ExpectedFinalOutputIndex,
            ExpectedFinalValueSats = request.ExpectedFinalValueSats,
            Status = request.ExpectedFinalTransactionId is null
                ? BTCPayServer.Plugins.Payjoin.Data.PayjoinAccountingBridgeStatus.PendingFallback
                : BTCPayServer.Plugins.Payjoin.Data.PayjoinAccountingBridgeStatus.PendingFinalTransaction,
            CreatedAt = now,
            UpdatedAt = now
        });
        await context.SaveChangesAsync(token).ConfigureAwait(false);
        return (await new PayjoinAccountingBridgeService(factory, new PayjoinSessionBuildLock()).TryGetByInvoiceIdAsync(request.InvoiceId, token).ConfigureAwait(false))!;
    }
}
