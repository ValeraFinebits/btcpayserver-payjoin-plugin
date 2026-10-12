using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinAttentionRecordSeederTests
{
    private const string SeededExpectedTransactionId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1";

    [Fact]
    public async Task TrySeedAttentionRecordAsyncCreatesAFailedRecordWhoseRetryKeepsTheOriginalDeadline()
    {
        using var context = new RelationalPluginTestContext();
        var seeder = context.CreateAttentionRecordSeeder();
        var bridgeService = context.CreateBridgeService();
        var seededAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var seededStatus = await seeder.TrySeedAttentionRecordAsync(
            CreateSeedRequest("invoice-seeded-failed", PayjoinAttentionRecordSeedKind.Failed, seededAt),
            CancellationToken.None);

        Assert.Equal(PayjoinAccountingBridgeStatus.Failed, seededStatus);
        var seeded = await bridgeService.TryGetByInvoiceIdAsync("invoice-seeded-failed", CancellationToken.None);
        Assert.NotNull(seeded);
        Assert.Equal(PayjoinAccountingBridgeStatus.Failed, seeded!.Status);
        Assert.StartsWith("SEEDED:", seeded.FailureMessage, StringComparison.Ordinal);
        Assert.Null(seeded.ExpectedFinalTransactionId);
        Assert.Null(seeded.ExpectedFinalOutputIndex);
        Assert.Null(seeded.ExpectedFinalValueSats);
        Assert.Equal(seededAt, seeded.UpdatedAt);
        Assert.Equal(seededAt + TimeSpan.FromHours(24), seeded.ExpiresAt);

        var attention = await bridgeService.GetRequiringAttentionAsync("store-1", CancellationToken.None);
        Assert.Contains(attention.Bridges, item => item.InvoiceId == seeded.InvoiceId);
        var retryAt = seededAt.AddHours(1);
        var retried = await bridgeService.TryRetryAsync(seeded.InvoiceId, "store-1", retryAt, CancellationToken.None);
        Assert.NotNull(retried);
        Assert.Equal(PayjoinAccountingBridgeStatus.PendingFallback, retried!.Status);
        Assert.Equal(seeded.ExpiresAt, retried.ExpiresAt);

        using var db = context.CreateDbContext();
        var persisted = await db.AccountingBridges.AsNoTracking().SingleAsync(x => x.InvoiceId == seeded.InvoiceId, TestContext.Current.CancellationToken);
        Assert.Equal(PayjoinAccountingBridgeStatus.PendingFallback, persisted.Status);
        Assert.Null(persisted.ExpectedFinalTransactionId);
        Assert.Null(persisted.ExpectedFinalOutputIndex);
        Assert.Null(persisted.ExpectedFinalValueSats);
        Assert.Null(persisted.FailureMessage);
        Assert.Equal(seeded.ExpiresAt, persisted.ExpiresAt);
        Assert.Equal(retryAt, persisted.UpdatedAt);
    }

    [Fact]
    public async Task TrySeedAttentionRecordAsyncCreatesAnExpiredRecordWhoseRetryPreservesItsTransactionFields()
    {
        using var context = new RelationalPluginTestContext();
        var seeder = context.CreateAttentionRecordSeeder();
        var bridgeService = context.CreateBridgeService();
        var seededAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await bridgeService.CreateOrGetAsync(
            new CreatePayjoinAccountingBridgeRequest("invoice-unrelated", "store-1", PayjoinConstants.BitcoinCode, "BTC-BTC", seededAt.AddHours(2)),
            CancellationToken.None);
        var unrelated = await bridgeService.TryGetByInvoiceIdAsync("invoice-unrelated", CancellationToken.None);
        Assert.NotNull(unrelated);

        var seededStatus = await seeder.TrySeedAttentionRecordAsync(
            CreateSeedRequest("invoice-seeded-expired", PayjoinAttentionRecordSeedKind.Expired, seededAt),
            CancellationToken.None);

        Assert.Equal(PayjoinAccountingBridgeStatus.Expired, seededStatus);
        var seeded = await bridgeService.TryGetByInvoiceIdAsync("invoice-seeded-expired", CancellationToken.None);
        Assert.NotNull(seeded);
        Assert.Equal(PayjoinAccountingBridgeStatus.Expired, seeded!.Status);
        Assert.StartsWith("SEEDED:", seeded.FailureMessage, StringComparison.Ordinal);
        Assert.Equal(SeededExpectedTransactionId, seeded.ExpectedFinalTransactionId);
        Assert.Equal(0, seeded.ExpectedFinalOutputIndex);
        Assert.Equal(1000, seeded.ExpectedFinalValueSats);
        Assert.Equal(seededAt, seeded.UpdatedAt);
        Assert.Equal(seededAt - PayjoinAccountingBridgeService.ArmedBridgeGracePeriod - TimeSpan.FromMinutes(1), seeded.ExpiresAt);

        var unrelatedAfterSeed = await bridgeService.TryGetByInvoiceIdAsync("invoice-unrelated", CancellationToken.None);
        Assert.Equal(unrelated, unrelatedAfterSeed);
        var attention = await bridgeService.GetRequiringAttentionAsync("store-1", CancellationToken.None);
        Assert.Contains(attention.Bridges, item => item.InvoiceId == seeded.InvoiceId);

        var retryAt = seededAt.AddHours(1);
        var retried = await bridgeService.TryRetryAsync(seeded.InvoiceId, "store-1", retryAt, CancellationToken.None);
        Assert.NotNull(retried);
        Assert.Equal(PayjoinAccountingBridgeStatus.PendingFinalTransaction, retried!.Status);
        Assert.Equal(SeededExpectedTransactionId, retried.ExpectedFinalTransactionId);
        Assert.Equal(0, retried.ExpectedFinalOutputIndex);
        Assert.Equal(1000, retried.ExpectedFinalValueSats);
        Assert.Equal(retryAt + PayjoinAccountingBridgeService.ArmedBridgeGracePeriod, retried.ExpiresAt);
        var attentionAfterRetry = await bridgeService.GetRequiringAttentionAsync("store-1", CancellationToken.None);
        Assert.DoesNotContain(attentionAfterRetry.Bridges, item => item.InvoiceId == seeded.InvoiceId);

        using var db = context.CreateDbContext();
        var persisted = await db.AccountingBridges.AsNoTracking().SingleAsync(x => x.InvoiceId == seeded.InvoiceId, TestContext.Current.CancellationToken);
        Assert.Equal(PayjoinAccountingBridgeStatus.PendingFinalTransaction, persisted.Status);
        Assert.Equal(SeededExpectedTransactionId, persisted.ExpectedFinalTransactionId);
        Assert.Equal(0, persisted.ExpectedFinalOutputIndex);
        Assert.Equal(1000, persisted.ExpectedFinalValueSats);
        Assert.Null(persisted.FailureMessage);
        Assert.Equal(retryAt + PayjoinAccountingBridgeService.ArmedBridgeGracePeriod, persisted.ExpiresAt);
        Assert.Equal(retryAt, persisted.UpdatedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrySeedAttentionRecordAsyncRefusesToOverwriteAnExistingRecord(bool expired)
    {
        using var context = new RelationalPluginTestContext();
        var seeder = context.CreateAttentionRecordSeeder();
        var bridgeService = context.CreateBridgeService();
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await bridgeService.CreateOrGetAsync(
            new CreatePayjoinAccountingBridgeRequest(
                "invoice-existing", "store-1", PayjoinConstants.BitcoinCode, "BTC-BTC", now.AddHours(1),
                ExpectedFinalTransactionId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                ExpectedFinalOutputIndex: 1,
                ExpectedFinalValueSats: 950),
            CancellationToken.None);
        var existing = await bridgeService.TryGetByInvoiceIdAsync("invoice-existing", CancellationToken.None);
        Assert.NotNull(existing);

        var seededStatus = await seeder.TrySeedAttentionRecordAsync(
            CreateSeedRequest("invoice-existing", expired ? PayjoinAttentionRecordSeedKind.Expired : PayjoinAttentionRecordSeedKind.Failed, now),
            CancellationToken.None);

        Assert.Null(seededStatus);
        var existingAfterSeed = await bridgeService.TryGetByInvoiceIdAsync("invoice-existing", CancellationToken.None);
        Assert.Equal(existing, existingAfterSeed);
    }

    private static SeedPayjoinAttentionRecordRequest CreateSeedRequest(
        string invoiceId,
        PayjoinAttentionRecordSeedKind kind,
        DateTimeOffset seededAt)
    {
        return new SeedPayjoinAttentionRecordRequest(invoiceId, "store-1", PayjoinConstants.BitcoinCode, "BTC-BTC", kind, seededAt);
    }
}
