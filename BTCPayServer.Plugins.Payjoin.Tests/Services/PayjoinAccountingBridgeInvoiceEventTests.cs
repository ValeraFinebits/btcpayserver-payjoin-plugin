using BTCPayServer.Logging;
using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinAccountingBridgeInvoiceEventTests
{
    private const string InvoiceId = "invoice-journal";
    private const string TransactionId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task LifecyclePublishesCommittedTransitionsAndRetainsEventsForNewRetryAttempts()
    {
        using var context = new RelationalPluginTestContext();
        using var events = new EventAggregator(new Logs());
        var captured = new List<PayjoinInvoiceEvent>();
        using var subscription = events.Subscribe<PayjoinInvoiceEvent>(evt =>
        {
            using var persisted = context.CreateDbContext();
            var bridge = Assert.Single(persisted.AccountingBridges.Where(row => row.InvoiceId == evt.InvoiceId));
            if (evt.Kind == PayjoinInvoiceEventKind.Failed)
            {
                Assert.Equal(PayjoinAccountingBridgeStatus.Failed, bridge.Status);
            }
            if (evt.Kind == PayjoinInvoiceEventKind.Reconciled)
            {
                Assert.Equal(PayjoinAccountingBridgeStatus.Reconciled, bridge.Status);
                Assert.Equal(TransactionId, bridge.ExpectedFinalTransactionId);
            }
            captured.Add(evt);
        });
        var publisher = new PayjoinInvoiceEventPublisher(events, NullLogger<PayjoinInvoiceEventPublisher>.Instance);
        var service = context.CreateBridgeService(publisher);
        var store = context.CreateStore(publisher);
        var request = CreateRequest(DateTimeOffset.UtcNow.AddHours(1));
        context.CreateTrackedSession(request, publisher);
        context.CreateTrackedSession(request, publisher);
        Assert.Equal(PayjoinInvoiceEventKind.TrackingStarted, Assert.Single(captured).Kind);
        Assert.True(store.RemoveSession(InvoiceId));
        using (var seed = context.CreateDbContext())
        {
            var bridge = seed.AccountingBridges.Single();
            bridge.ExpectedFinalTransactionId = TransactionId;
            bridge.ExpectedFinalOutputIndex = 0;
            bridge.ExpectedFinalValueSats = 1000;
            bridge.Status = PayjoinAccountingBridgeStatus.PendingFinalTransaction;
            seed.SaveChanges();
        }

        await service.MarkFailedAsync(InvoiceId, "mismatched output", TestContext.Current.CancellationToken);
        await service.MarkFailedAsync(InvoiceId, "mismatched output", TestContext.Current.CancellationToken);
        Assert.Null(await service.TryRetryAsync(InvoiceId, "wrong-store", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));
        Assert.NotNull(await service.TryRetryAsync(InvoiceId, "store-1", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));
        Assert.Null(await service.TryRetryAsync(InvoiceId, "store-1", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));
        await service.MarkFailedAsync(InvoiceId, "mismatched output again", TestContext.Current.CancellationToken);
        Assert.NotNull(await service.TryRetryAsync(InvoiceId, "store-1", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));
        await service.MarkReconciledAsync(InvoiceId, TransactionId, 0, 1000, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        await service.MarkReconciledAsync(InvoiceId, TransactionId, 0, 1000, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        var restarted = context.CreateBridgeService(publisher);
        Assert.NotNull(await restarted.TryGetByInvoiceIdAsync(InvoiceId, TestContext.Current.CancellationToken));
        await restarted.MarkReconciledAsync(InvoiceId, TransactionId, 0, 1000, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        Assert.Equal(new[]
        {
            PayjoinInvoiceEventKind.TrackingStarted,
            PayjoinInvoiceEventKind.Failed,
            PayjoinInvoiceEventKind.RetryRequested,
            PayjoinInvoiceEventKind.Failed,
            PayjoinInvoiceEventKind.RetryRequested,
            PayjoinInvoiceEventKind.Reconciled
        }, captured.Select(evt => evt.Kind));
        var success = Assert.Single(captured.Single(evt => evt.Kind == PayjoinInvoiceEventKind.Reconciled).Logs.ToList());
        Assert.Contains("Settled with Payjoin", success.Log, StringComparison.Ordinal);
        Assert.DoesNotContain("Settled with Async Payjoin", success.Log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpiryPublishesOnceAndDoesNotClaimAnyPaymentOrCompletedProtocolOutcome()
    {
        using var context = new RelationalPluginTestContext();
        using var events = new EventAggregator(new Logs());
        var captured = new List<PayjoinInvoiceEvent>();
        using var subscription = events.Subscribe<PayjoinInvoiceEvent>(captured.Add);
        var publisher = new PayjoinInvoiceEventPublisher(events, NullLogger<PayjoinInvoiceEventPublisher>.Instance);
        var service = context.CreateBridgeService(publisher);
        var now = DateTimeOffset.UtcNow;
        context.CreateTrackedSession(CreateRequest(now.AddMinutes(-1)), publisher);
        context.CreateTrackedSession(CreateRequest(now.AddHours(-7)) with
        {
            InvoiceId = "invoice-armed",
        }, publisher);
        var store = context.CreateStore(publisher);
        store.BackfillProposalExpectation(store.Owner("invoice-armed"),
            new PayjoinReceiverSessionStore.ProposalSettlement(TransactionId, 0, 1000));

        await service.ExpirePendingAsync(now, TestContext.Current.CancellationToken);
        await service.ExpirePendingAsync(now, TestContext.Current.CancellationToken);

        var expired = captured.Where(evt => evt.Kind == PayjoinInvoiceEventKind.Expired).ToArray();
        Assert.Equal(2, expired.Length);
        var unarmed = Assert.Single(expired.Single(evt => evt.InvoiceId == InvoiceId).Logs.ToList());
        Assert.Equal(PayjoinInvoiceEventPublisher.MessagePrefix + "Payjoin tracking expired.", unarmed.Log);
        var armed = Assert.Single(expired.Single(evt => evt.InvoiceId == "invoice-armed").Logs.ToList());
        Assert.Contains(TransactionId, armed.Log, StringComparison.Ordinal);
        Assert.Equal(BTCPayServer.Data.InvoiceEventData.EventSeverity.Warning, armed.Severity);
        foreach (var evt in expired)
        {
            var log = Assert.Single(evt.Logs.ToList());
            Assert.DoesNotContain("was paid", log.Log, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("did not complete", log.Log, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ExistingSessionRetainsThePinnedAccountingAndPublishesNoSecondStart()
    {
        using var context = new RelationalPluginTestContext();
        using var events = new EventAggregator(new Logs());
        var captured = new List<PayjoinInvoiceEvent>();
        using var subscription = events.Subscribe<PayjoinInvoiceEvent>(captured.Add);
        var publisher = new PayjoinInvoiceEventPublisher(events, NullLogger<PayjoinInvoiceEventPublisher>.Instance);
        var store = context.CreateStore(publisher);
        var request = CreateRequest(DateTimeOffset.UtcNow.AddHours(1));
        var original = context.CreateTrackedSession(request, publisher);
        store.AttachOriginalPayment(store.Owner(InvoiceId),
            new PayjoinReceiverPaymentValidation.OriginalPayment(1000, TransactionId, 0));

        var retained = context.CreateTrackedSession(request with { EffectiveInvoiceValueSats = 1200 }, publisher);
        context.CreateTrackedSession(request with { EffectiveInvoiceValueSats = 1200 }, publisher);

        Assert.Equal(original.SessionInstanceId, retained.SessionInstanceId);
        Assert.Equal(PayjoinInvoiceEventKind.TrackingStarted, Assert.Single(captured).Kind);
        using var persisted = context.CreateDbContext();
        var bridge = Assert.Single(persisted.AccountingBridges);
        Assert.Equal(TransactionId, bridge.FallbackTransactionId);
        Assert.Null(bridge.ExpectedFinalTransactionId);
        Assert.Equal(1000, bridge.EffectiveInvoiceValueSats);
    }

    [Fact]
    public async Task PersistenceFailurePublishesNoEventAndLeavesThePersistedStateUnchanged()
    {
        using var context = new RelationalPluginTestContext();
        using var events = new EventAggregator(new Logs());
        var captured = new List<PayjoinInvoiceEvent>();
        using var subscription = events.Subscribe<PayjoinInvoiceEvent>(captured.Add);
        var publisher = new PayjoinInvoiceEventPublisher(events, NullLogger<PayjoinInvoiceEventPublisher>.Instance);
        var service = context.CreateBridgeService(publisher);
        context.FailSaveChanges = true;
        Assert.Throws<DbUpdateException>(() => context.CreateTrackedSession(CreateRequest(DateTimeOffset.UtcNow.AddHours(1)), publisher));
        Assert.Empty(captured);
        context.FailSaveChanges = false;
        context.CreateTrackedSession(CreateRequest(DateTimeOffset.UtcNow.AddHours(1)), publisher);
        captured.Clear();
        context.FailSaveChanges = true;
        await Assert.ThrowsAsync<DbUpdateException>(() => service.MarkFailedAsync(InvoiceId, "injected", TestContext.Current.CancellationToken));
        context.FailSaveChanges = false;

        Assert.Empty(captured);
        using var persisted = context.CreateDbContext();
        Assert.Equal(PayjoinAccountingBridgeStatus.PendingFallback, Assert.Single(persisted.AccountingBridges).Status);
    }

    private static CreatePayjoinAccountingBridgeRequest CreateRequest(DateTimeOffset expiresAt) =>
        new(InvoiceId, "store-1", PayjoinConstants.BitcoinCode, "BTC-BTC", expiresAt, EffectiveInvoiceValueSats: 1000);
}
