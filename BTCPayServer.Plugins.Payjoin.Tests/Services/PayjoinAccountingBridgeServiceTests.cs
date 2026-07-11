using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinAccountingBridgeServiceTests
{
    private const string ExpectedTransactionId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task ExpirePendingAsyncExpiresUnarmedBridgesAtTheDeadline()
    {
        using var context = new TestContext();
        var service = context.CreateService();
        var now = DateTimeOffset.UtcNow;
        await CreateBridgeAsync(service, "invoice-unarmed", expiresAt: now.AddMinutes(-1));

        var expired = await service.ExpirePendingAsync(now, CancellationToken.None);

        var bridge = Assert.Single(expired);
        Assert.Equal("invoice-unarmed", bridge.InvoiceId);
        Assert.Equal(PayjoinAccountingBridgeStatus.Expired, bridge.Status);
    }

    [Fact]
    public async Task ExpirePendingAsyncKeepsArmedBridgesAliveWithinTheGracePeriod()
    {
        using var context = new TestContext();
        var service = context.CreateService();
        var now = DateTimeOffset.UtcNow;
        await CreateBridgeAsync(service, "invoice-armed", expiresAt: now.AddMinutes(-1), expectedFinalTransactionId: ExpectedTransactionId);

        var expired = await service.ExpirePendingAsync(now, CancellationToken.None);

        Assert.Empty(expired);
        var pending = await service.GetPendingAsync(now, CancellationToken.None);
        Assert.Contains(pending, x => x.InvoiceId == "invoice-armed");
    }

    [Fact]
    public async Task ExpirePendingAsyncExpiresArmedBridgesAfterTheGracePeriod()
    {
        using var context = new TestContext();
        var service = context.CreateService();
        var now = DateTimeOffset.UtcNow;
        var pastGrace = now - PayjoinAccountingBridgeService.ArmedBridgeGracePeriod - TimeSpan.FromMinutes(1);
        await CreateBridgeAsync(service, "invoice-armed", expiresAt: pastGrace, expectedFinalTransactionId: ExpectedTransactionId);

        var expired = await service.ExpirePendingAsync(now, CancellationToken.None);

        var bridge = Assert.Single(expired);
        Assert.Equal("invoice-armed", bridge.InvoiceId);
        Assert.Equal(ExpectedTransactionId, bridge.ExpectedFinalTransactionId);
    }

    [Fact]
    public async Task GetRequiringAttentionAsyncReturnsFailedAndArmedExpiredBridgesOnly()
    {
        using var context = new TestContext();
        var service = context.CreateService();
        var now = DateTimeOffset.UtcNow;
        await CreateBridgeAsync(service, "invoice-pending", expiresAt: now.AddHours(1));
        await CreateBridgeAsync(service, "invoice-failed", expiresAt: now.AddHours(1));
        await service.MarkFailedAsync("invoice-failed", "reconciliation data problem", CancellationToken.None);
        var pastGrace = now - PayjoinAccountingBridgeService.ArmedBridgeGracePeriod - TimeSpan.FromMinutes(1);
        await CreateBridgeAsync(service, "invoice-armed-expired", expiresAt: pastGrace, expectedFinalTransactionId: ExpectedTransactionId);
        await CreateBridgeAsync(service, "invoice-unarmed-expired", expiresAt: now.AddMinutes(-5));
        await service.ExpirePendingAsync(now, CancellationToken.None);

        var attention = await service.GetRequiringAttentionAsync("store-1", CancellationToken.None);

        Assert.Equal(2, attention.Count);
        Assert.Contains(attention, x => x.InvoiceId == "invoice-failed" && x.Status == PayjoinAccountingBridgeStatus.Failed);
        Assert.Contains(attention, x => x.InvoiceId == "invoice-armed-expired" && x.Status == PayjoinAccountingBridgeStatus.Expired);
    }

    [Fact]
    public async Task TryRetryAsyncResetsAFailedBridgeForAnotherReconciliationWindow()
    {
        using var context = new TestContext();
        var service = context.CreateService();
        var now = DateTimeOffset.UtcNow;
        await CreateBridgeAsync(service, "invoice-failed", expiresAt: now.AddHours(1), expectedFinalTransactionId: ExpectedTransactionId);
        await service.MarkFailedAsync("invoice-failed", "reconciliation data problem", CancellationToken.None);

        var retried = await service.TryRetryAsync("invoice-failed", "store-1", now, CancellationToken.None);

        Assert.NotNull(retried);
        Assert.Equal(PayjoinAccountingBridgeStatus.PendingFinalTransaction, retried!.Status);
        Assert.Null(retried.FailureMessage);
        Assert.Equal(now + PayjoinAccountingBridgeService.ArmedBridgeGracePeriod, retried.ExpiresAt);
        var pending = await service.GetPendingAsync(now, CancellationToken.None);
        Assert.Contains(pending, x => x.InvoiceId == "invoice-failed");
    }

    [Fact]
    public async Task TryRetryAsyncKeepsTheOriginalDeadlineForFailedUnarmedBridges()
    {
        using var context = new TestContext();
        var service = context.CreateService();
        var now = DateTimeOffset.UtcNow;
        var originalDeadline = now.AddHours(1);
        await CreateBridgeAsync(service, "invoice-failed", expiresAt: originalDeadline);
        await service.MarkFailedAsync("invoice-failed", "reconciliation data problem", CancellationToken.None);

        var retried = await service.TryRetryAsync("invoice-failed", "store-1", now, CancellationToken.None);

        // Without an expected final transaction there is nothing for the grace period to
        // outlive, so the bridge resumes waiting for a fallback under its original deadline.
        Assert.NotNull(retried);
        Assert.Equal(PayjoinAccountingBridgeStatus.PendingFallback, retried!.Status);
        Assert.Null(retried.FailureMessage);
        Assert.Equal(originalDeadline, retried.ExpiresAt);
    }

    [Fact]
    public async Task TryRetryAsyncRefusesWrongStoreAndNonTerminalStatuses()
    {
        using var context = new TestContext();
        var service = context.CreateService();
        var now = DateTimeOffset.UtcNow;
        await CreateBridgeAsync(service, "invoice-failed", expiresAt: now.AddHours(1));
        await service.MarkFailedAsync("invoice-failed", "reconciliation data problem", CancellationToken.None);
        await CreateBridgeAsync(service, "invoice-pending", expiresAt: now.AddHours(1));

        Assert.Null(await service.TryRetryAsync("invoice-failed", "other-store", now, CancellationToken.None));
        Assert.Null(await service.TryRetryAsync("invoice-pending", "store-1", now, CancellationToken.None));
        Assert.Null(await service.TryRetryAsync("missing-invoice", "store-1", now, CancellationToken.None));
    }

    [Fact]
    public async Task TryRetryAsyncRefusesExpiredUnarmedBridges()
    {
        using var context = new TestContext();
        var service = context.CreateService();
        var now = DateTimeOffset.UtcNow;
        await CreateBridgeAsync(service, "invoice-unarmed", expiresAt: now.AddMinutes(-5));
        await service.ExpirePendingAsync(now, CancellationToken.None);

        // Matches the attention surface: an expired bridge that never armed has nothing left
        // to reconcile, so it is terminal and cannot be retried.
        Assert.Null(await service.TryRetryAsync("invoice-unarmed", "store-1", now, CancellationToken.None));
    }

    [Fact]
    public async Task TryRetryAsyncRetriesExpiredArmedBridges()
    {
        using var context = new TestContext();
        var service = context.CreateService();
        var now = DateTimeOffset.UtcNow;
        var pastGrace = now - PayjoinAccountingBridgeService.ArmedBridgeGracePeriod - TimeSpan.FromMinutes(1);
        await CreateBridgeAsync(service, "invoice-armed", expiresAt: pastGrace, expectedFinalTransactionId: ExpectedTransactionId);
        await service.ExpirePendingAsync(now, CancellationToken.None);

        var retried = await service.TryRetryAsync("invoice-armed", "store-1", now, CancellationToken.None);

        Assert.NotNull(retried);
        Assert.Equal(PayjoinAccountingBridgeStatus.PendingFinalTransaction, retried!.Status);
        Assert.Equal(now + PayjoinAccountingBridgeService.ArmedBridgeGracePeriod, retried.ExpiresAt);
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
        private readonly TestPayjoinPluginDbContextFactory _dbContextFactory = new();
        private readonly PostgresPayjoinUniqueConstraintViolationDetector _uniqueConstraintViolationDetector = new();

        public PayjoinAccountingBridgeService CreateService() => new(_dbContextFactory, _uniqueConstraintViolationDetector);

        public void Dispose()
        {
            using var db = _dbContextFactory.CreateContext();
            db.Database.EnsureDeleted();
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
            var databaseName = $"payjoin-bridge-service-tests-{Guid.NewGuid():N}";
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
