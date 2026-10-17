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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountingDecisionFromOlderBridgeSnapshotCannotOverwriteNewerEvidence(bool failed)
    {
        using var fixture = new RelationalPluginTestContext();
        var service = fixture.CreateBridgeService();
        await CreateBridgeAsync(fixture.SeedBridgeAsync, "stale-accounting", DateTimeOffset.UtcNow.AddHours(1));
        var original = await service.TryGetByInvoiceIdAsync("stale-accounting", CancellationToken.None);
        await service.MarkFailedAsync("stale-accounting", "new evidence", CancellationToken.None);
        var current = await service.TryGetByInvoiceIdAsync("stale-accounting", CancellationToken.None);
        var result = failed
            ? await service.MarkFailedAsync("stale-accounting", "old decision", CancellationToken.None, expectedUpdatedAt: original!.UpdatedAt)
            : await service.MarkReconciledAsync("stale-accounting", ExpectedTransactionId, 0, 950, DateTimeOffset.UtcNow,
                CancellationToken.None, expectedUpdatedAt: original!.UpdatedAt);
        Assert.Null(result);
        Assert.Equal(current, await service.TryGetByInvoiceIdAsync("stale-accounting", CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProtectingWritesRetryFreshSnapshotsAndNeverPartiallySaveBridge(bool exhaust)
    {
        using var fixture = new RelationalPluginTestContext();
        var store = fixture.CreateStore();
        var service = fixture.CreateBridgeService();
        var now = DateTimeOffset.UtcNow;
        store.GetOrCreateSession("race", "receiver", "store-1", now.AddHours(1), ["original", "proposal"]);
        await CreateBridgeAsync(fixture.SeedBridgeAsync, "race", now.AddHours(1));
        var conflicts = 0;
        void ConcurrentWrite()
        {
            using var context = fixture.CreateDbContext();
            store.CreatePersister(store.GetAttempt("race", context.ReceiverSessions.Single().SessionInstanceId)).Save("competing " + conflicts);
            Assert.Equal(PayjoinAccountingBridgeStatus.PendingFallback, context.AccountingBridges.Single().Status);
            conflicts++;
            if (exhaust && conflicts < 3) fixture.BeforeNextSaveChanges = ConcurrentWrite;
        }
        fixture.BeforeNextSaveChanges = ConcurrentWrite;
        Task<PayjoinAccountingBridgeState?> Write() => service.MarkFailedAsync("race", "diagnostic", CancellationToken.None);
        if (exhaust) await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => Write());
        else Assert.NotNull(await Write());
        var saved = await service.TryGetByInvoiceIdAsync("race", CancellationToken.None);
        Assert.NotNull(saved);
        Assert.Equal(exhaust ? PayjoinAccountingBridgeStatus.PendingFallback : PayjoinAccountingBridgeStatus.Failed, saved.Status);
        Assert.Null(saved.ExpectedFinalTransactionId);
        Assert.Equal(!exhaust ? "diagnostic" : null, saved.FailureMessage);
        using var verification = fixture.CreateDbContext();
        Assert.Equal(exhaust ? 3 : 2, verification.ReceiverSessions.Single().AttemptVersion);
        Assert.Equal(new[] { "original", "proposal" }.Concat(Enumerable.Range(0, conflicts).Select(index => "competing " + index)).ToArray(), verification.ReceiverSessionEvents.OrderBy(e => e.Sequence).Select(e => e.Event).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryCannotReviveFailedBridgeWhileAnySessionIsHeld(bool armed)
    {
        using var fixture = new RelationalPluginTestContext();
        var service = fixture.CreateBridgeService();
        var now = DateTimeOffset.UtcNow;
        await CreateBridgeAsync(fixture.SeedBridgeAsync, "invoice-held", now.AddMinutes(1), armed ? ExpectedTransactionId : null);
        fixture.CreateStore().GetOrCreateSession("invoice-held", "receiver", "store-1", now.AddMinutes(1), ["original", "proposal"]);
        await service.MarkFailedAsync("invoice-held", "opaque diagnostic", CancellationToken.None);
        var before = await service.TryGetByInvoiceIdAsync("invoice-held", CancellationToken.None);
        Assert.Null(await service.TryRetryAsync("invoice-held", "store-1", now.AddDays(1), CancellationToken.None));
        Assert.Equal(before, await service.TryGetByInvoiceIdAsync("invoice-held", CancellationToken.None));
    }

    [Fact]
    public async Task ExpirePendingAsyncExpiresUnarmedBridgesAtTheDeadline()
    {
        using var context = new SessionStoreFixture();
        var service = context.CreateBridgeService();
        var now = DateTimeOffset.UtcNow;
        await CreateBridgeAsync(context.SeedBridgeAsync, "invoice-unarmed", expiresAt: now.AddMinutes(-1));

        var expired = await service.ExpirePendingAsync(now, CancellationToken.None);

        var bridge = Assert.Single(expired);
        Assert.Equal("invoice-unarmed", bridge.InvoiceId);
        Assert.Equal(PayjoinAccountingBridgeStatus.Expired, bridge.Status);
    }

    [Fact]
    public async Task ExpirePendingAsyncKeepsArmedBridgesAliveWithinTheGracePeriod()
    {
        using var context = new SessionStoreFixture();
        var service = context.CreateBridgeService();
        var now = DateTimeOffset.UtcNow;
        await CreateBridgeAsync(context.SeedBridgeAsync, "invoice-armed", expiresAt: now.AddMinutes(-1), expectedFinalTransactionId: ExpectedTransactionId);

        var expired = await service.ExpirePendingAsync(now, CancellationToken.None);

        Assert.Empty(expired);
        var pending = await service.GetPendingReconciliationAsync(now, CancellationToken.None);
        Assert.Contains(pending, x => x.InvoiceId == "invoice-armed");
    }

    [Fact]
    public async Task ExpirePendingAsyncExpiresArmedBridgesAfterTheGracePeriod()
    {
        using var context = new SessionStoreFixture();
        var service = context.CreateBridgeService();
        var now = DateTimeOffset.UtcNow;
        var pastGrace = now - PayjoinAccountingBridgeService.ArmedBridgeGracePeriod - TimeSpan.FromMinutes(1);
        await CreateBridgeAsync(context.SeedBridgeAsync, "invoice-armed", expiresAt: pastGrace, expectedFinalTransactionId: ExpectedTransactionId);

        var expired = await service.ExpirePendingAsync(now, CancellationToken.None);

        var bridge = Assert.Single(expired);
        Assert.Equal("invoice-armed", bridge.InvoiceId);
        Assert.Equal(ExpectedTransactionId, bridge.ExpectedFinalTransactionId);
    }

    [Fact]
    public async Task GetRequiringAttentionAsyncReturnsFailedAndArmedExpiredBridgesOnly()
    {
        using var context = new SessionStoreFixture();
        var service = context.CreateBridgeService();
        var now = DateTimeOffset.UtcNow;
        await CreateBridgeAsync(context.SeedBridgeAsync, "invoice-pending", expiresAt: now.AddHours(1));
        await CreateBridgeAsync(context.SeedBridgeAsync, "invoice-failed", expiresAt: now.AddHours(1));
        await service.MarkFailedAsync("invoice-failed", "reconciliation data problem", CancellationToken.None);
        var pastGrace = now - PayjoinAccountingBridgeService.ArmedBridgeGracePeriod - TimeSpan.FromMinutes(1);
        await CreateBridgeAsync(context.SeedBridgeAsync, "invoice-armed-expired", expiresAt: pastGrace, expectedFinalTransactionId: ExpectedTransactionId);
        await CreateBridgeAsync(context.SeedBridgeAsync, "invoice-unarmed-expired", expiresAt: now.AddMinutes(-5));
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
        using var context = new SessionStoreFixture();
        var service = context.CreateBridgeService();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < PayjoinAccountingBridgeService.AttentionListLimit + 1; i++)
        {
            var invoiceId = $"invoice-failed-{i}";
            await CreateBridgeAsync(context.SeedBridgeAsync, invoiceId, expiresAt: now.AddHours(1));
            await service.MarkFailedAsync(invoiceId, "reconciliation data problem", CancellationToken.None);
        }

        var attention = await service.GetRequiringAttentionAsync("store-1", CancellationToken.None);

        Assert.Equal(PayjoinAccountingBridgeService.AttentionListLimit, attention.Bridges.Count);
        Assert.Equal(PayjoinAccountingBridgeService.AttentionListLimit + 1, attention.TotalCount);
    }

    [Theory]
    [InlineData("reconciliation data problem")]
    [InlineData("SEEDED: reconciliation data problem")]
    public async Task TryRetryAsyncPreservesAFailedBridgesTransactionFields(string failureMessage)
    {
        using var context = new RelationalPluginTestContext();
        var service = context.CreateBridgeService();
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        const string fallbackTransactionId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string settlementScript = "00141111111111111111111111111111111111111111";
        await context.SeedBridgeAsync(new CreatePayjoinAccountingBridgeRequest("invoice-failed", "store-1", "BTC", "BTC-BTC", now.AddHours(1),
            FallbackTransactionId: fallbackTransactionId, FallbackOutputIndex: 0, FallbackValueSats: 900, EffectiveInvoiceValueSats: 900, SettlementScript: settlementScript,
            ExpectedFinalTransactionId: ExpectedTransactionId, ExpectedFinalOutputIndex: 1, ExpectedFinalValueSats: 950), CancellationToken.None);
        await service.MarkFailedAsync("invoice-failed", failureMessage, CancellationToken.None);
        DateTimeOffset failedWriteTime;
        using (var beforeRetry = context.CreateDbContext())
            failedWriteTime = (await beforeRetry.AccountingBridges.AsNoTracking().SingleAsync(x => x.InvoiceId == "invoice-failed", Xunit.TestContext.Current.CancellationToken)).UpdatedAt;

        var retried = await service.TryRetryAsync("invoice-failed", "store-1", now, CancellationToken.None);

        Assert.NotNull(retried);
        Assert.Equal(PayjoinAccountingBridgeStatus.PendingFinalTransaction, retried!.Status);
        Assert.Equal(ExpectedTransactionId, retried.ExpectedFinalTransactionId);
        Assert.Equal(1, retried.ExpectedFinalOutputIndex);
        Assert.Equal(950, retried.ExpectedFinalValueSats);
        Assert.Null(retried.FailureMessage);
        Assert.Equal(now + PayjoinAccountingBridgeService.ArmedBridgeGracePeriod, retried.ExpiresAt);
        var pending = await service.GetPendingReconciliationAsync(now, CancellationToken.None);
        Assert.Contains(pending, x => x.InvoiceId == "invoice-failed");

        using var db = context.CreateDbContext();
        var persisted = await db.AccountingBridges.AsNoTracking().SingleAsync(x => x.InvoiceId == "invoice-failed", Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(PayjoinAccountingBridgeStatus.PendingFinalTransaction, persisted.Status);
        Assert.Equal(ExpectedTransactionId, persisted.ExpectedFinalTransactionId);
        Assert.Equal(1, persisted.ExpectedFinalOutputIndex);
        Assert.Equal(950, persisted.ExpectedFinalValueSats);
        Assert.Equal(fallbackTransactionId, persisted.FallbackTransactionId);
        Assert.Equal(0, persisted.FallbackOutputIndex);
        Assert.Equal(900, persisted.FallbackValueSats);
        Assert.Equal(900, persisted.EffectiveInvoiceValueSats);
        Assert.Equal(settlementScript, persisted.SettlementScript);
        Assert.Null(persisted.FailureMessage);
        Assert.Equal(now + PayjoinAccountingBridgeService.ArmedBridgeGracePeriod, persisted.ExpiresAt);
        Assert.True(persisted.UpdatedAt >= now);
        Assert.True(retried.UpdatedAt > failedWriteTime);
        Assert.True(persisted.UpdatedAt.UtcTicks / 1000 >= failedWriteTime.UtcTicks / 1000);
    }

    [Fact]
    public async Task TryRetryAsyncKeepsTheOriginalDeadlineForFailedUnarmedBridges()
    {
        using var context = new RelationalPluginTestContext();
        var service = context.CreateBridgeService();
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var originalDeadline = now.AddHours(1);
        await CreateBridgeAsync(context.SeedBridgeAsync, "invoice-failed", expiresAt: originalDeadline);
        await service.MarkFailedAsync("invoice-failed", "reconciliation data problem", CancellationToken.None);

        var retried = await service.TryRetryAsync("invoice-failed", "store-1", now, CancellationToken.None);

        // Without an expected final transaction there is nothing for the grace period to
        // outlive, so the bridge resumes waiting for a fallback under its original deadline.
        Assert.NotNull(retried);
        Assert.Equal(PayjoinAccountingBridgeStatus.PendingFallback, retried!.Status);
        Assert.Null(retried.FailureMessage);
        Assert.Equal(originalDeadline, retried.ExpiresAt);

        using var db = context.CreateDbContext();
        var persisted = await db.AccountingBridges.AsNoTracking().SingleAsync(x => x.InvoiceId == "invoice-failed", Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(PayjoinAccountingBridgeStatus.PendingFallback, persisted.Status);
        Assert.Null(persisted.ExpectedFinalTransactionId);
        Assert.Null(persisted.ExpectedFinalOutputIndex);
        Assert.Null(persisted.ExpectedFinalValueSats);
        Assert.Null(persisted.FailureMessage);
        Assert.Equal(originalDeadline, persisted.ExpiresAt);
    }

    [Fact]
    public async Task TryRetryAsyncRefusesWrongStoreAndNonTerminalStatuses()
    {
        using var context = new SessionStoreFixture();
        var service = context.CreateBridgeService();
        var now = DateTimeOffset.UtcNow;
        await CreateBridgeAsync(context.SeedBridgeAsync, "invoice-failed", expiresAt: now.AddHours(1));
        await service.MarkFailedAsync("invoice-failed", "reconciliation data problem", CancellationToken.None);
        await CreateBridgeAsync(context.SeedBridgeAsync, "invoice-pending", expiresAt: now.AddHours(1));

        Assert.Null(await service.TryRetryAsync("invoice-failed", "other-store", now, CancellationToken.None));
        Assert.Null(await service.TryRetryAsync("invoice-pending", "store-1", now, CancellationToken.None));
        Assert.Null(await service.TryRetryAsync("missing-invoice", "store-1", now, CancellationToken.None));
    }

    [Fact]
    public async Task TryRetryAsyncRefusesExpiredUnarmedBridges()
    {
        using var context = new SessionStoreFixture();
        var service = context.CreateBridgeService();
        var now = DateTimeOffset.UtcNow;
        await CreateBridgeAsync(context.SeedBridgeAsync, "invoice-unarmed", expiresAt: now.AddMinutes(-5));
        await service.ExpirePendingAsync(now, CancellationToken.None);

        // Matches the attention surface: an expired bridge that never armed has nothing left
        // to reconcile, so it is terminal and cannot be retried.
        Assert.Null(await service.TryRetryAsync("invoice-unarmed", "store-1", now, CancellationToken.None));
    }

    [Fact]
    public async Task TryRetryAsyncRetriesExpiredArmedBridges()
    {
        using var context = new RelationalPluginTestContext();
        var service = context.CreateBridgeService();
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var pastGrace = now - PayjoinAccountingBridgeService.ArmedBridgeGracePeriod - TimeSpan.FromMinutes(1);
        await CreateBridgeAsync(context.SeedBridgeAsync, "invoice-armed", expiresAt: pastGrace, expectedFinalTransactionId: ExpectedTransactionId);
        await service.ExpirePendingAsync(now, CancellationToken.None);

        var retried = await service.TryRetryAsync("invoice-armed", "store-1", now, CancellationToken.None);

        Assert.NotNull(retried);
        Assert.Equal(PayjoinAccountingBridgeStatus.PendingFinalTransaction, retried!.Status);
        Assert.Equal(ExpectedTransactionId, retried.ExpectedFinalTransactionId);
        Assert.Equal(1, retried.ExpectedFinalOutputIndex);
        Assert.Equal(950, retried.ExpectedFinalValueSats);
        Assert.Equal(now + PayjoinAccountingBridgeService.ArmedBridgeGracePeriod, retried.ExpiresAt);

        using var db = context.CreateDbContext();
        var persisted = await db.AccountingBridges.AsNoTracking().SingleAsync(x => x.InvoiceId == "invoice-armed", Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(PayjoinAccountingBridgeStatus.PendingFinalTransaction, persisted.Status);
        Assert.Equal(ExpectedTransactionId, persisted.ExpectedFinalTransactionId);
        Assert.Equal(1, persisted.ExpectedFinalOutputIndex);
        Assert.Equal(950, persisted.ExpectedFinalValueSats);
        Assert.Equal(now + PayjoinAccountingBridgeService.ArmedBridgeGracePeriod, persisted.ExpiresAt);
    }

    private static Task<PayjoinAccountingBridgeState> CreateBridgeAsync(
        Func<CreatePayjoinAccountingBridgeRequest, CancellationToken, Task<PayjoinAccountingBridgeState>> seed,
        string invoiceId,
        DateTimeOffset? expiresAt,
        string? expectedFinalTransactionId = null)
    {
        return seed(
            new CreatePayjoinAccountingBridgeRequest(
                invoiceId,
                "store-1",
                PayjoinConstants.BitcoinCode,
                "BTC-BTC",
                expiresAt,
                EffectiveInvoiceValueSats: 1000,
                ExpectedFinalTransactionId: expectedFinalTransactionId,
                ExpectedFinalOutputIndex: expectedFinalTransactionId is null ? null : 1,
                ExpectedFinalValueSats: expectedFinalTransactionId is null ? null : 950),
            CancellationToken.None);
    }

}
