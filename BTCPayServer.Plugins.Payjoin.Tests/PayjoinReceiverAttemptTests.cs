using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.EntityFrameworkCore;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class PayjoinReceiverAttemptTests
{
    [Fact]
    public async Task RetryVisibilityMatchesTheHeldFailureBackendRule()
    {
        using var fixture = new RelationalPluginTestContext();
        var store = fixture.CreateStore();
        var bridge = fixture.CreateBridgeService();
        foreach (var invoice in new[] { "held", "unheld" })
        {
            await fixture.SeedBridgeAsync(new CreatePayjoinAccountingBridgeRequest(invoice, "store", "BTC", "BTC-CHAIN", DateTimeOffset.UtcNow.AddHours(1)), CancellationToken.None);
            await bridge.MarkFailedAsync(invoice, "opaque reason", CancellationToken.None);
        }
        store.GetOrCreateSession("held", "receiver", "store", DateTimeOffset.UtcNow.AddHours(1), ["history"]);
        var attention = await new PayjoinBridgeAttentionService(bridge).GetRequiringAttentionAsync("store", CancellationToken.None);
        Assert.False(Assert.Single(attention.Items, item => item.InvoiceId == "held").RetryAllowed);
        Assert.True(Assert.Single(attention.Items, item => item.InvoiceId == "unheld").RetryAllowed);
        Assert.Null(await bridge.TryRetryAsync("held", "store", DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.NotNull(await bridge.TryRetryAsync("unheld", "store", DateTimeOffset.UtcNow, CancellationToken.None));
    }

    [Theory]
    [InlineData("event")]
    [InlineData("reserve")]
    [InlineData("close")]
    [InlineData("consume")]
    [InlineData("uri")]
    [InlineData("delete")]
    [InlineData("discard")]
    [InlineData("failed")]
    [InlineData("fallback")]
    [InlineData("expected")]
    [InlineData("outputs")]
    public async Task OldAttemptCannotReadOrMutateReplacement(string operation)
    {
        using var fixture = new RelationalPluginTestContext();
        var store = fixture.CreateStore();
        var bridges = fixture.CreateBridgeService();
        var old = store.GetOrCreateSession("invoice", "receiver", "store", DateTimeOffset.UtcNow.AddHours(1), ["old bootstrap"]);
        var persister = store.CreatePersister(old);
        using var trackedContext = fixture.CreateDbContext();
        var tracked = trackedContext.ReceiverSessions.Single();
        Assert.True(store.RemoveSession(old.InvoiceId, old.SessionInstanceId));
        var replacement = store.GetOrCreateSession("invoice", "receiver", "store", DateTimeOffset.UtcNow.AddHours(1), ["new bootstrap", "new payload"]);
        store.StorePayjoinUri("invoice", "receiver", "bitcoin:receiver?amount=0.0008&pj=https://example.com/new", replacement.SessionInstanceId);
        await fixture.SeedBridgeAsync(new CreatePayjoinAccountingBridgeRequest("invoice", "store", "BTC", "BTC-CHAIN", replacement.MonitoringExpiresAt, EffectiveInvoiceValueSats: 80_000), CancellationToken.None);
        var bridge = await bridges.TryGetByInvoiceIdAsync("invoice", CancellationToken.None);
        Assert.NotEqual(old.SessionInstanceId, replacement.SessionInstanceId);
        Assert.NotEqual(Guid.Empty, replacement.SessionInstanceId);
        Assert.Throws<PayjoinSessionStaleException>(() => persister.Load());
        tracked.PayjoinUri = "stale tracked entity";
        Assert.Throws<DbUpdateConcurrencyException>(() => trackedContext.SaveChanges());

        async Task Write()
        {
            switch (operation)
            {
                case "event": persister.Save("old event"); break;
                case "reserve": store.TryReserveContributedInput(store.CreatePersister(old), "store", new OutPoint(uint256.One, 0), DateTimeOffset.UtcNow.AddHours(1)); break;
                case "close": store.RequestClose("invoice", Client.Models.InvoiceStatus.Expired, old.SessionInstanceId); break;
                case "consume": store.TryConsumeInitializedPollAfterCloseRequest("invoice", old.SessionInstanceId); break;
                case "uri": store.StorePayjoinUri("invoice", "receiver", "old URI", old.SessionInstanceId); break;
                case "delete": store.RemoveSession("invoice", old.SessionInstanceId); break;
                case "discard": store.TryRemoveSessionUnlessNegotiating("invoice", old.SessionInstanceId); break;
                case "failed": await bridges.MarkFailedAsync("invoice", "stale failure", CancellationToken.None, old.SessionInstanceId).ConfigureAwait(false); break;
                case "fallback": store.AttachOriginalPayment(store.CreatePersister(old), new PayjoinReceiverPaymentValidation.OriginalPayment(50_000, uint256.One.ToString(), 0)); break;
                case "expected":
                    await new PayjoinReceiverProposalFinalizer(null!, null!, bridges, store, null!).EnsureExpectedFinalTransactionAsync(
                    new PayjoinReceiverProposalFinalizationContext(persister, "store", "invoice", "BTC"), null!, CancellationToken.None).ConfigureAwait(false); break;
                case "outputs": store.CommitReceiverOutputs(store.CreatePersister(old), ["old outputs"], new PayjoinReceiverOutputBuilder.OutputReplacement([], Convert.FromHexString("51"), 50_000, null)); break;
            }
        }
        await Assert.ThrowsAsync<PayjoinSessionStaleException>(Write);
        var saved = store.GetAttempt("invoice", replacement.SessionInstanceId);
        Assert.Equal(new[] { "new bootstrap", "new payload" }, saved.GetEvents());
        Assert.Equal("bitcoin:receiver?amount=0.0008&pj=https://example.com/new", saved.PayjoinUri);
        Assert.False(saved.IsCloseRequested);
        Assert.False(saved.TryGetContributedInput(out _));
        Assert.Equal(bridge, await bridges.TryGetByInvoiceIdAsync("invoice", CancellationToken.None));
        using var verification = fixture.CreateDbContext();
        Assert.Empty(verification.ReceiverInputReservations);
    }

    [Fact]
    public void AConcurrentAppendInvalidatesTheOldPersisterWithinTheSameAttempt()
    {
        using var fixture = new RelationalPluginTestContext();
        var store = fixture.CreateStore();
        var state = store.GetOrCreateSession("invoice", "receiver", "store", DateTimeOffset.UtcNow.AddHours(1), ["bootstrap"]);
        var stale = store.CreatePersister(state);
        store.CreatePersister(state).Save("winner");
        Assert.Throws<PayjoinSessionStaleException>(() => stale.Save("loser"));
        Assert.Equal(new[] { "bootstrap", "winner" }, store.GetAttempt("invoice", state.SessionInstanceId).GetEvents());
    }
}
