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
    public async Task TrySeedAttentionRecordAsyncCreatesAMarkedFailedRecord()
    {
        using var context = new TestContext();
        var service = context.CreateService();
        var now = DateTimeOffset.UtcNow;

        var seededStatus = await service.TrySeedAttentionRecordAsync(
            CreateSeedRequest("invoice-seeded-failed", PayjoinAttentionRecordSeedKind.Failed, now),
            CancellationToken.None);

        Assert.Equal(PayjoinAccountingBridgeStatus.Failed, seededStatus);
        var bridge = await service.TryGetByInvoiceIdAsync("invoice-seeded-failed", CancellationToken.None);
        Assert.NotNull(bridge);
        Assert.Equal(PayjoinAccountingBridgeStatus.Failed, bridge!.Status);
        Assert.StartsWith("SEEDED:", bridge.FailureMessage, StringComparison.Ordinal);
        Assert.Null(bridge.ExpectedFinalTransactionId);
        Assert.Equal(now, bridge.UpdatedAt);
        Assert.Equal(now + TimeSpan.FromHours(24), bridge.ExpiresAt);

        var attention = await service.GetRequiringAttentionAsync("store-1", CancellationToken.None);
        Assert.Contains(attention.Bridges, item => item.InvoiceId == bridge.InvoiceId);
        var retried = await service.TryRetryAsync(bridge.InvoiceId, "store-1", now, CancellationToken.None);
        Assert.Equal(PayjoinAccountingBridgeStatus.PendingFallback, retried!.Status);
        Assert.Null(retried.ExpectedFinalTransactionId);
        Assert.Equal(now + TimeSpan.FromHours(24), retried.ExpiresAt);
    }

    [Fact]
    public async Task TrySeedAttentionRecordAsyncCreatesAMarkedExpiredRecordWithoutSweepingOtherInvoices()
    {
        using var context = new TestContext();
        var service = context.CreateService();
        var now = DateTimeOffset.UtcNow;
        var unrelated = await CreateBridgeAsync(service, "invoice-unrelated", expiresAt: now.AddHours(2));

        var seededStatus = await service.TrySeedAttentionRecordAsync(
            CreateSeedRequest("invoice-seeded-expired", PayjoinAttentionRecordSeedKind.Expired, now),
            CancellationToken.None);

        Assert.Equal(PayjoinAccountingBridgeStatus.Expired, seededStatus);
        var seeded = await service.TryGetByInvoiceIdAsync("invoice-seeded-expired", CancellationToken.None);
        Assert.NotNull(seeded);
        Assert.Equal(PayjoinAccountingBridgeStatus.Expired, seeded!.Status);
        Assert.StartsWith("SEEDED:", seeded.FailureMessage, StringComparison.Ordinal);
        Assert.Equal(ExpectedTransactionId[..^1] + "1", seeded.ExpectedFinalTransactionId);
        Assert.Equal(0, seeded.ExpectedFinalOutputIndex);
        Assert.Equal(1000, seeded.ExpectedFinalValueSats);
        Assert.Equal(now, seeded.UpdatedAt);
        Assert.Equal(now - PayjoinAccountingBridgeService.ArmedBridgeGracePeriod - TimeSpan.FromMinutes(1), seeded.ExpiresAt);

        var unrelatedAfterSeed = await service.TryGetByInvoiceIdAsync("invoice-unrelated", CancellationToken.None);
        Assert.Equal(unrelated, unrelatedAfterSeed);

        var attention = await service.GetRequiringAttentionAsync("store-1", CancellationToken.None);
        Assert.Contains(attention.Bridges, item => item.InvoiceId == seeded.InvoiceId);
        var retried = await service.TryRetryAsync(seeded.InvoiceId, "store-1", now, CancellationToken.None);
        Assert.Equal(PayjoinAccountingBridgeStatus.PendingFallback, retried!.Status);
        Assert.Null(retried.ExpectedFinalTransactionId);
        Assert.Null(retried.ExpectedFinalOutputIndex);
        Assert.Null(retried.ExpectedFinalValueSats);
        Assert.Equal(now + TimeSpan.FromHours(24), retried.ExpiresAt);

        var attentionAfterRetry = await service.GetRequiringAttentionAsync("store-1", CancellationToken.None);
        Assert.DoesNotContain(attentionAfterRetry.Bridges, item => item.InvoiceId == seeded.InvoiceId);
    }

    [Fact]
    public async Task TrySeedAttentionRecordAsyncRefusesToOverwriteAnExistingRecord()
    {
        using var context = new TestContext();
        var service = context.CreateService();
        var now = DateTimeOffset.UtcNow;
        var existing = await CreateBridgeAsync(
            service,
            "invoice-existing",
            expiresAt: now.AddHours(1),
            expectedFinalTransactionId: ExpectedTransactionId);

        var seededStatus = await service.TrySeedAttentionRecordAsync(
            CreateSeedRequest("invoice-existing", PayjoinAttentionRecordSeedKind.Failed, now),
            CancellationToken.None);

        Assert.Null(seededStatus);
        var existingAfterSeed = await service.TryGetByInvoiceIdAsync("invoice-existing", CancellationToken.None);
        Assert.Equal(existing, existingAfterSeed);
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

        Assert.Equal(2, attention.TotalCount);
        Assert.Equal(2, attention.Bridges.Count);
        Assert.Contains(attention.Bridges, x => x.InvoiceId == "invoice-failed" && x.Status == PayjoinAccountingBridgeStatus.Failed);
        Assert.Contains(attention.Bridges, x => x.InvoiceId == "invoice-armed-expired" && x.Status == PayjoinAccountingBridgeStatus.Expired);
    }

    [Fact]
    public async Task GetRequiringAttentionAsyncBoundsTheListAndReportsTheTotal()
    {
        using var context = new TestContext();
        var service = context.CreateService();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < PayjoinAccountingBridgeService.AttentionListLimit + 1; i++)
        {
            var invoiceId = $"invoice-failed-{i}";
            await CreateBridgeAsync(service, invoiceId, expiresAt: now.AddHours(1));
            await service.MarkFailedAsync(invoiceId, "reconciliation data problem", CancellationToken.None);
        }

        var attention = await service.GetRequiringAttentionAsync("store-1", CancellationToken.None);

        Assert.Equal(PayjoinAccountingBridgeService.AttentionListLimit, attention.Bridges.Count);
        Assert.Equal(PayjoinAccountingBridgeService.AttentionListLimit + 1, attention.TotalCount);
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

    private static SeedPayjoinAttentionRecordRequest CreateSeedRequest(
        string invoiceId,
        PayjoinAttentionRecordSeedKind kind,
        DateTimeOffset seededAt)
    {
        return new SeedPayjoinAttentionRecordRequest(
            invoiceId,
            "store-1",
            PayjoinConstants.BitcoinCode,
            "BTC-BTC",
            kind,
            seededAt);
    }

    private sealed class TestContext : IDisposable
    {
        private readonly TestPayjoinPluginDbContextFactory _dbContextFactory = new();
        private readonly PostgresPayjoinUniqueConstraintViolationDetector _uniqueConstraintViolationDetector = new();

        public PayjoinSessionBuildLock SessionBuildLock { get; } = new();

        public PayjoinAccountingBridgeService CreateService() => new(_dbContextFactory, _uniqueConstraintViolationDetector, SessionBuildLock);

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
