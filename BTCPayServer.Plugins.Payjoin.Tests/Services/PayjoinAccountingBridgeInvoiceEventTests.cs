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
        var request = CreateRequest(DateTimeOffset.UtcNow.AddHours(1));
        await service.CreateOrGetAsync(request, TestContext.Current.CancellationToken);
        await service.CreateOrGetAsync(request, TestContext.Current.CancellationToken);
        await service.SetExpectedFinalTransactionAsync(InvoiceId, TransactionId, 0, 1000, TestContext.Current.CancellationToken);
        Assert.Equal(PayjoinInvoiceEventKind.TrackingStarted, Assert.Single(captured).Kind);

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
        await restarted.CreateOrGetAsync(request, TestContext.Current.CancellationToken);
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
        await service.CreateOrGetAsync(CreateRequest(now.AddMinutes(-1)), TestContext.Current.CancellationToken);
        await service.CreateOrGetAsync(CreateRequest(now.AddHours(-7)) with
        {
            InvoiceId = "invoice-armed",
            ExpectedFinalTransactionId = TransactionId
        }, TestContext.Current.CancellationToken);

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
    public async Task NewSessionResetPublishesOnlyWhenItActuallyResetsTracking()
    {
        using var context = new RelationalPluginTestContext();
        using var events = new EventAggregator(new Logs());
        var captured = new List<PayjoinInvoiceEvent>();
        using var subscription = events.Subscribe<PayjoinInvoiceEvent>(captured.Add);
        var publisher = new PayjoinInvoiceEventPublisher(events, NullLogger<PayjoinInvoiceEventPublisher>.Instance);
        var service = context.CreateBridgeService(publisher);
        await service.CreateOrGetAsync(CreateRequest(DateTimeOffset.UtcNow.AddHours(1)), TestContext.Current.CancellationToken);
        await service.AttachFallbackAsync(InvoiceId, TransactionId, 0, 1000, 1000, "0014", TestContext.Current.CancellationToken);

        await service.ResetForNewSessionAsync(InvoiceId, 1200, DateTimeOffset.UtcNow.AddHours(2), TestContext.Current.CancellationToken);
        await service.ResetForNewSessionAsync(InvoiceId, 1200, DateTimeOffset.UtcNow.AddHours(2), TestContext.Current.CancellationToken);

        Assert.Equal(2, captured.Count);
        Assert.All(captured, evt => Assert.Equal(PayjoinInvoiceEventKind.TrackingStarted, evt.Kind));
        using var persisted = context.CreateDbContext();
        var bridge = Assert.Single(persisted.AccountingBridges);
        Assert.Null(bridge.FallbackTransactionId);
        Assert.Null(bridge.ExpectedFinalTransactionId);
        Assert.Equal(1200, bridge.EffectiveInvoiceValueSats);
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
        await Assert.ThrowsAsync<DbUpdateException>(() => service.CreateOrGetAsync(CreateRequest(DateTimeOffset.UtcNow.AddHours(1)), TestContext.Current.CancellationToken));
        Assert.Empty(captured);
        context.FailSaveChanges = false;
        await service.CreateOrGetAsync(CreateRequest(DateTimeOffset.UtcNow.AddHours(1)), TestContext.Current.CancellationToken);
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
