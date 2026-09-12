using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinAccountingBridgeResetTests
{
    private const string ExpectedTransactionId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string FallbackTransactionId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task ResetForNewSessionAsyncClearsPriorSessionTrackingOnUnarmedPendingBridges()
    {
        using var context = new TestContext();
        var service = context.CreateService();
        var now = DateTimeOffset.UtcNow;
        await CreateBridgeAsync(service, "invoice-1", now.AddHours(1));
        await service.AttachFallbackAsync("invoice-1", FallbackTransactionId, 0, 900, 900, "CCDD", CancellationToken.None);
        using (var db = context.CreateDbContext())
        {
            db.AccountingBridges.Single(x => x.InvoiceId == "invoice-1").SettlementKeyPath = "1/42";
            db.SaveChanges();
        }

        var reset = await service.ResetForNewSessionAsync("invoice-1", 1200, now.AddHours(2), CancellationToken.None);

        Assert.NotNull(reset);
        Assert.Equal(PayjoinAccountingBridgeStatus.PendingFallback, reset!.Status);
        Assert.Null(reset.FallbackTransactionId);
        Assert.Null(reset.SettlementScript);
        Assert.Null(reset.SettlementKeyPath);
        Assert.Null(reset.ExpectedFinalTransactionId);
        Assert.Null(reset.ExpectedFinalOutputIndex);
        Assert.Null(reset.ExpectedFinalValueSats);
        Assert.Equal(1200, reset.EffectiveInvoiceValueSats);
        Assert.Equal(now.AddHours(2), reset.ExpiresAt);
    }

    [Fact]
    public async Task ResetForNewSessionAsyncPreservesArmedBridgesSoTheOldProposalStillReconciles()
    {
        // The previous session already handed a signed proposal to the sender: the expected final
        // transaction is the only handle that makes that settlement creditable, so recreating the
        // session must not wipe it. The old accounting flow stays live until the new session
        // produces its own finalized proposal and overwrites the expectation itself.
        using var context = new TestContext();
        var service = context.CreateService();
        var now = DateTimeOffset.UtcNow;
        await CreateBridgeAsync(service, "invoice-1", now.AddHours(1));
        await service.AttachFallbackAsync("invoice-1", FallbackTransactionId, 0, 900, 900, "CCDD", CancellationToken.None);
        await service.SetExpectedFinalTransactionAsync("invoice-1", ExpectedTransactionId, 1, 950, CancellationToken.None);

        var reset = await service.ResetForNewSessionAsync("invoice-1", 1200, now.AddHours(2), CancellationToken.None);

        Assert.NotNull(reset);
        Assert.Equal(PayjoinAccountingBridgeStatus.PendingFinalTransaction, reset!.Status);
        Assert.Equal(ExpectedTransactionId, reset.ExpectedFinalTransactionId);
        Assert.Equal(FallbackTransactionId, reset.FallbackTransactionId);
        Assert.Equal("CCDD", reset.SettlementScript);

        // The old session's final transaction becomes observable afterwards and still reconciles.
        var reconciled = await service.MarkReconciledAsync("invoice-1", ExpectedTransactionId, 1, 950, now, CancellationToken.None);
        Assert.Equal(PayjoinAccountingBridgeStatus.Reconciled, reconciled!.Status);
        Assert.Equal(ExpectedTransactionId, reconciled.ExpectedFinalTransactionId);
    }

    [Fact]
    public async Task ResetForNewSessionAsyncRevivesUnarmedExpiredBridges()
    {
        using var context = new TestContext();
        var service = context.CreateService();
        var now = DateTimeOffset.UtcNow;
        await CreateBridgeAsync(service, "invoice-1", now.AddMinutes(-5));
        await service.ExpirePendingAsync(now, CancellationToken.None);

        var reset = await service.ResetForNewSessionAsync("invoice-1", 1000, now.AddHours(1), CancellationToken.None);

        Assert.NotNull(reset);
        Assert.Equal(PayjoinAccountingBridgeStatus.PendingFallback, reset!.Status);
        Assert.Equal(now.AddHours(1), reset.ExpiresAt);
    }

    [Fact]
    public async Task ResetForNewSessionAsyncLeavesArmedExpiredAndFailedBridgesForReview()
    {
        using var context = new TestContext();
        var service = context.CreateService();
        var now = DateTimeOffset.UtcNow;
        await CreateBridgeAsync(service, "invoice-armed", now.AddHours(1), ExpectedTransactionId);
        await CreateBridgeAsync(service, "invoice-failed", now.AddHours(1));
        await service.MarkFailedAsync("invoice-failed", "problem", CancellationToken.None);
        using (var db = context.CreateDbContext())
        {
            var armed = db.AccountingBridges.Single(x => x.InvoiceId == "invoice-armed");
            armed.Status = PayjoinAccountingBridgeStatus.Expired;
            db.SaveChanges();
        }

        var armedResult = await service.ResetForNewSessionAsync("invoice-armed", 1000, now.AddHours(2), CancellationToken.None);
        var failedResult = await service.ResetForNewSessionAsync("invoice-failed", 1000, now.AddHours(2), CancellationToken.None);

        Assert.Equal(PayjoinAccountingBridgeStatus.Expired, armedResult!.Status);
        Assert.Equal(ExpectedTransactionId, armedResult.ExpectedFinalTransactionId);
        Assert.Equal(PayjoinAccountingBridgeStatus.Failed, failedResult!.Status);
        Assert.Equal("problem", failedResult.FailureMessage);
    }

    [Fact]
    public async Task ResetForNewSessionAsyncLeavesFreshPendingBridgesUntouched()
    {
        using var context = new TestContext();
        var service = context.CreateService();
        var now = DateTimeOffset.UtcNow;
        var created = await CreateBridgeAsync(service, "invoice-1", now.AddHours(1));

        var reset = await service.ResetForNewSessionAsync("invoice-1", 5000, now.AddHours(3), CancellationToken.None);

        Assert.NotNull(reset);
        Assert.Equal(created.EffectiveInvoiceValueSats, reset!.EffectiveInvoiceValueSats);
        Assert.Equal(created.ExpiresAt, reset.ExpiresAt);
    }

    [Fact]
    public async Task ExpirePendingAsyncCannotOverwriteARevivalPerformedUnderTheSessionBuildLock()
    {
        // The recreation flow revives a bridge while holding the invoice's session build lock.
        // Expiry takes the same lock per invoice and re-checks the deadline inside it, so an expiry
        // pass whose candidate snapshot predates the revival must skip instead of marking the
        // revived bridge Expired.
        using var context = new TestContext();
        var service = context.CreateService();
        var now = DateTimeOffset.UtcNow;
        await CreateBridgeAsync(service, "invoice-race", now.AddMinutes(-1));
        await service.AttachFallbackAsync("invoice-race", FallbackTransactionId, 0, 900, 900, "CCDD", CancellationToken.None);

        Task<System.Collections.Generic.IReadOnlyCollection<PayjoinAccountingBridgeState>> expiryTask;
        using (await context.SessionBuildLock.AcquireAsync("invoice-race", CancellationToken.None).ConfigureAwait(true))
        {
            expiryTask = service.ExpirePendingAsync(now, CancellationToken.None);
            await service.ResetForNewSessionAsync("invoice-race", 1200, now.AddHours(2), CancellationToken.None);
        }

        var expired = await expiryTask.ConfigureAwait(true);

        Assert.Empty(expired);
        var state = await service.TryGetByInvoiceIdAsync("invoice-race", CancellationToken.None);
        Assert.NotNull(state);
        Assert.Equal(PayjoinAccountingBridgeStatus.PendingFallback, state!.Status);
        Assert.Equal(now.AddHours(2), state.ExpiresAt);
    }

    [Fact]
    public async Task ResetForNewSessionAsyncIsIdempotentAcrossACrashRetry()
    {
        // Session creation resets the bridge before it persists the session, so a crash between the
        // two steps makes the retry reset again. The second reset must leave the same state behind.
        using var context = new TestContext();
        var service = context.CreateService();
        var now = DateTimeOffset.UtcNow;
        await CreateBridgeAsync(service, "invoice-retry", now.AddHours(1));
        await service.AttachFallbackAsync("invoice-retry", FallbackTransactionId, 0, 900, 900, "CCDD", CancellationToken.None);

        var first = await service.ResetForNewSessionAsync("invoice-retry", 1200, now.AddHours(2), CancellationToken.None);
        var second = await service.ResetForNewSessionAsync("invoice-retry", 1200, now.AddHours(2), CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(PayjoinAccountingBridgeStatus.PendingFallback, second!.Status);
        Assert.Null(second.FallbackTransactionId);
        Assert.Null(second.SettlementScript);
        Assert.Null(second.SettlementKeyPath);
        Assert.Null(second.ExpectedFinalTransactionId);
        Assert.Equal(first!.ExpiresAt, second.ExpiresAt);
        Assert.Equal(1200, second.EffectiveInvoiceValueSats);
    }

    private static Task<PayjoinAccountingBridgeState> CreateBridgeAsync(
        PayjoinAccountingBridgeService service,
        string invoiceId,
        DateTimeOffset? expiresAt,
        string? expectedFinalTransactionId = null)
    {
        return service.CreateOrGetAsync(
            new CreatePayjoinAccountingBridgeRequest(
                invoiceId,
                "store-1",
                PayjoinConstants.BitcoinCode,
                "BTC-BTC",
                expiresAt,
                EffectiveInvoiceValueSats: 1000,
                ExpectedFinalTransactionId: expectedFinalTransactionId),
            CancellationToken.None);
    }

    private sealed class TestContext : IDisposable
    {
        private readonly TestDbContextFactory _dbContextFactory = new();
        private readonly PostgresPayjoinUniqueConstraintViolationDetector _uniqueConstraintViolationDetector = new();

        public PayjoinSessionBuildLock SessionBuildLock { get; } = new();

        public PayjoinAccountingBridgeService CreateService() => new(_dbContextFactory, _uniqueConstraintViolationDetector, SessionBuildLock);

        public PayjoinPluginDbContext CreateDbContext() => _dbContextFactory.CreateContext();

        public void Dispose()
        {
            using var db = _dbContextFactory.CreateContext();
            db.Database.EnsureDeleted();
        }
    }

    private sealed class TestDbContextFactory : PayjoinPluginDbContextFactory
    {
        private static readonly InMemoryDatabaseRoot SharedDatabaseRoot = new();
        private readonly DbContextOptions<PayjoinPluginDbContext> _dbContextOptions;

        public TestDbContextFactory()
            : base(Options.Create(new DatabaseOptions
            {
                ConnectionString = "Host=localhost;Database=payjoin-plugin-tests;Username=postgres"
            }))
        {
            var databaseName = $"payjoin-bridge-reset-tests-{Guid.NewGuid():N}";
            _dbContextOptions = new DbContextOptionsBuilder<PayjoinPluginDbContext>()
                .UseInMemoryDatabase(databaseName, SharedDatabaseRoot)
                .Options;

            using var db = CreateContext();
            db.Database.EnsureCreated();
        }

        public override PayjoinPluginDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptionsAction = null)
        {
            return new PayjoinPluginDbContext(_dbContextOptions);
        }
    }
}
