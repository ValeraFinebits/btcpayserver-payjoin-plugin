using BTCPayServer.Plugins.Payjoin.Services;
using NBitcoin;
using System.Linq;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class PayjoinReceiverSessionStoreRelationalTests
{
    [Fact]
    public void MarkSeenAndWasPresentReportsRepeatedOutpointsAsSeen()
    {
        // Arrange
        using var testContext = new RelationalPluginTestContext();
        var store = testContext.CreateSeenInputStore();
        var transactionId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        // Act + Assert: first sighting is new, repeat is reported as seen, a different vout is new again.
        Assert.False(store.MarkSeenAndWasPresent(transactionId, 0));
        Assert.True(store.MarkSeenAndWasPresent(transactionId, 0));
        Assert.False(store.MarkSeenAndWasPresent(transactionId, 1));

        using var context = testContext.CreateDbContext();
        Assert.Equal(2, context.ReceiverSeenInputs.Count());
    }

    [Fact]
    public void TryReserveContributedInputAllowsOnlyOneReservationPerOutPointOnRelationalProvider()
    {
        // Arrange
        using var testContext = new RelationalPluginTestContext();
        var firstStore = testContext.CreateStore();
        var secondStore = testContext.CreateStore();
        var firstSession = CreateSession(firstStore, "invoice-relational-first");
        var secondSession = CreateSession(secondStore, "invoice-relational-second");
        var outPoint = new OutPoint(uint256.Parse("dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"), 1);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);

        // Act
        Assert.True(firstStore.TryReserveContributedInput(firstSession.StoreId, firstSession.InvoiceId, outPoint, expiresAt));
        Assert.False(secondStore.TryReserveContributedInput(secondSession.StoreId, secondSession.InvoiceId, outPoint, expiresAt));

        // Assert
        using var context = testContext.CreateDbContext();
        var reservation = Assert.Single(context.ReceiverInputReservations);
        Assert.Equal(firstSession.InvoiceId, reservation.InvoiceId);
        Assert.Equal(outPoint.Hash.ToString(), reservation.TransactionId);
        Assert.Equal((long)outPoint.N, reservation.OutputIndex);
    }

    [Fact]
    public async Task TryReserveContributedInputAllowsOnlyOneWinnerUnderConcurrentRequestsOnRelationalProvider()
    {
        // Arrange
        using var testContext = new RelationalPluginTestContext();
        var firstStore = testContext.CreateStore();
        var secondStore = testContext.CreateStore();
        var firstSession = CreateSession(firstStore, "invoice-relational-concurrent-first");
        var secondSession = CreateSession(secondStore, "invoice-relational-concurrent-second");
        var outPoint = new OutPoint(uint256.Parse("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), 2);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);

        using var start = new ManualResetEventSlim(false);
        var firstAttempt = Task.Run(() =>
        {
            start.Wait();
            return firstStore.TryReserveContributedInput(firstSession.StoreId, firstSession.InvoiceId, outPoint, expiresAt);
        });
        var secondAttempt = Task.Run(() =>
        {
            start.Wait();
            return secondStore.TryReserveContributedInput(secondSession.StoreId, secondSession.InvoiceId, outPoint, expiresAt);
        });

        // Act
        start.Set();
        var results = await Task.WhenAll(firstAttempt, secondAttempt).ConfigureAwait(true);

        // Assert
        Assert.Single(results, result => result);
        Assert.Single(results, result => !result);

        using var context = testContext.CreateDbContext();
        var reservation = Assert.Single(context.ReceiverInputReservations);
        Assert.Equal(outPoint.Hash.ToString(), reservation.TransactionId);
        Assert.Equal((long)outPoint.N, reservation.OutputIndex);
        Assert.Contains(reservation.InvoiceId, new[] { firstSession.InvoiceId, secondSession.InvoiceId });
    }

    [Fact]
    public void AppendEventsWithAccountingUpdateWritesEventsAndBridgeTogether()
    {
        using var testContext = new RelationalPluginTestContext();
        var store = testContext.CreateStore();
        var session = CreateSession(store, "invoice-atomic-append");
        using (var seed = testContext.CreateDbContext())
        {
            seed.AccountingBridges.Add(new Data.PayjoinAccountingBridgeData
            {
                InvoiceId = session.InvoiceId,
                StoreId = session.StoreId,
                CryptoCode = PayjoinConstants.BitcoinCode,
                PaymentMethodId = "BTC-BTC",
                Status = Data.PayjoinAccountingBridgeStatus.PendingFallback,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            seed.SaveChanges();
        }

        store.AppendEventsWithAccountingUpdate(
            session.InvoiceId,
            ["event-a", "event-b"],
            bridge =>
            {
                bridge.SettlementScript = "AABB";
                bridge.EffectiveInvoiceValueSats = 1234;
            });

        using var context = testContext.CreateDbContext();
        var events = context.ReceiverSessionEvents
            .Where(x => x.InvoiceId == session.InvoiceId)
            .OrderBy(x => x.Sequence)
            .ToArray();
        Assert.Equal(new[] { "bootstrap-event", "event-a", "event-b" }, events.Select(x => x.Event).ToArray());
        Assert.Equal(new[] { 1, 2, 3 }, events.Select(x => x.Sequence).ToArray());
        var bridge = Assert.Single(context.AccountingBridges.Where(x => x.InvoiceId == session.InvoiceId));
        Assert.Equal("AABB", bridge.SettlementScript);
        Assert.Equal(1234, bridge.EffectiveInvoiceValueSats);
    }

    [Fact]
    public void AppendEventsWithAccountingUpdateAppendsEventsWhenNoBridgeExists()
    {
        using var testContext = new RelationalPluginTestContext();
        var store = testContext.CreateStore();
        var session = CreateSession(store, "invoice-atomic-no-bridge");

        store.AppendEventsWithAccountingUpdate(session.InvoiceId, ["event-a"], bridge => bridge.SettlementScript = "AABB");

        using var context = testContext.CreateDbContext();
        var events = context.ReceiverSessionEvents
            .Where(x => x.InvoiceId == session.InvoiceId)
            .OrderBy(x => x.Sequence)
            .Select(x => x.Event)
            .ToArray();
        Assert.Equal(new[] { "bootstrap-event", "event-a" }, events);
        Assert.Empty(context.AccountingBridges.Where(x => x.InvoiceId == session.InvoiceId));
    }

    private static PayjoinReceiverSessionState CreateSession(PayjoinReceiverSessionStore store, string invoiceId)
    {
        return store.CreateSession(
            invoiceId,
            "bcrt1qexampleaddress0000000000000000000000000",
            "store-1",
            DateTimeOffset.UtcNow.AddMinutes(15),
            ["bootstrap-event"]);
    }
}
