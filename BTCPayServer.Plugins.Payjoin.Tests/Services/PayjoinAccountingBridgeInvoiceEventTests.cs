using BTCPayServer.Logging;
using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinAccountingBridgeInvoiceEventTests
{
    private const string InvoiceId = "invoice-journal";
    private const string TransactionId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task SessionlessBridgePublishesCommittedTransitionsAndRetainsEventsForNewRetryAttempts()
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
        await context.SeedBridgeAsync(request with { ExpectedFinalTransactionId = TransactionId }, TestContext.Current.CancellationToken);
        Assert.Empty(captured);

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
        await restarted.MarkReconciledAsync(InvoiceId, TransactionId, 0, 1000, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        Assert.Equal(new[]
        {
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
        await context.SeedBridgeAsync(CreateRequest(now.AddMinutes(-1)), TestContext.Current.CancellationToken);
        await context.SeedBridgeAsync(CreateRequest(now.AddHours(-7)) with
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
    public void NewSessionCreationPublishesOnlyOncePerAttempt()
    {
        using var context = new RelationalPluginTestContext();
        using var events = new EventAggregator(new Logs());
        var captured = new List<PayjoinInvoiceEvent>();
        using var subscription = events.Subscribe<PayjoinInvoiceEvent>(captured.Add);
        var publisher = new PayjoinInvoiceEventPublisher(events, NullLogger<PayjoinInvoiceEventPublisher>.Instance);
        var store = context.CreateStore(publisher);
        var first = CreateSession(store);
        Assert.Equal(first.SessionInstanceId, CreateSession(store).SessionInstanceId);
        Assert.Equal(PayjoinInvoiceEventKind.TrackingStarted, Assert.Single(captured).Kind);

        Assert.True(store.RemoveSession(InvoiceId, first.SessionInstanceId));
        var second = CreateSession(store, 1200);
        Assert.NotEqual(first.SessionInstanceId, second.SessionInstanceId);
        Assert.Equal(second.SessionInstanceId, CreateSession(store, 1200).SessionInstanceId);

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
        var store = context.CreateStore(publisher);
        context.FailSaveChanges = true;
        Assert.Throws<DbUpdateException>(() => CreateSession(store));
        Assert.Empty(captured);
        context.FailSaveChanges = false;
        var session = CreateSession(store);
        captured.Clear();
        context.FailSaveChanges = true;
        Assert.Throws<DbUpdateException>(() => store.FailPayment(store.CreatePersister(session), "injected"));
        Assert.Throws<DbUpdateException>(() => store.CommitProposal(store.CreatePersister(session), ["uncommitted proposal"],
            new PayjoinReceiverSessionStore.ProposalSettlement(TransactionId, 0, 1000)));
        await Assert.ThrowsAsync<DbUpdateException>(() => context.CreateBridgeService(publisher)
            .MarkFailedAsync(InvoiceId, "injected", TestContext.Current.CancellationToken));
        context.FailSaveChanges = false;

        Assert.Empty(captured);
        using var persisted = context.CreateDbContext();
        var bridge = Assert.Single(persisted.AccountingBridges);
        Assert.Equal(PayjoinAccountingBridgeStatus.PendingFallback, bridge.Status);
        Assert.Null(bridge.ExpectedFinalTransactionId);
        Assert.DoesNotContain(persisted.ReceiverSessionEvents, row => row.Event == "uncommitted proposal");
    }

    [Fact]
    public void ProtocolEventsFollowCommittedWritesAndIgnoreStaleOrRepeatedTransitions()
    {
        using var context = new RelationalPluginTestContext();
        using var events = new EventAggregator(new Logs());
        var captured = new List<PayjoinInvoiceEvent>();
        using var subscription = events.Subscribe<PayjoinInvoiceEvent>(evt =>
        {
            using var persisted = context.CreateDbContext();
            var bridge = Assert.Single(persisted.AccountingBridges);
            if (evt.Kind == PayjoinInvoiceEventKind.ProposalPrepared)
            {
                Assert.Equal(TransactionId, bridge.ExpectedFinalTransactionId);
                Assert.Contains(persisted.ReceiverSessionEvents, row => row.Event == "prepared proposal");
            }
            if (evt.Kind == PayjoinInvoiceEventKind.Failed)
                Assert.Equal(PayjoinAccountingBridgeStatus.Failed, bridge.Status);
            captured.Add(evt);
        });
        var publisher = new PayjoinInvoiceEventPublisher(events, NullLogger<PayjoinInvoiceEventPublisher>.Instance);
        var store = context.CreateStore(publisher);
        var session = CreateSession(store);
        var stale = store.CreatePersister(session);
        var owner = store.CreatePersister(session);
        var settlement = new PayjoinReceiverSessionStore.ProposalSettlement(TransactionId, 0, 1000);
        store.CommitProposal(owner, ["prepared proposal"], settlement);
        store.BackfillProposalExpectation(owner, settlement);
        Assert.Throws<PayjoinSessionStaleException>(() => store.FailPayment(stale, "stale failure"));
        store.FailPayment(owner, "mismatched output");
        store.FailPayment(owner, "mismatched output");

        Assert.Equal(new[]
        {
            PayjoinInvoiceEventKind.TrackingStarted,
            PayjoinInvoiceEventKind.ProposalPrepared,
            PayjoinInvoiceEventKind.Failed
        }, captured.Select(evt => evt.Kind));
    }

    private static PayjoinReceiverSessionState CreateSession(PayjoinReceiverSessionStore store, ulong amountSats = 1000)
    {
        using var key = new Key();
        var address = key.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest).ToString();
        using var keys = global::Payjoin.OhttpKeys.Decode(Convert.FromHexString(
            "01001604ba48c49c3d4a92a3ad00ecc63a024da10ced02180c73ec12d8a7ad2cc91bb483824fe2bee8d28bfe2eb2fc6453bc4d31cd851e8a6540e86c5382af588d370957000400010003"));
        using var builder = new global::Payjoin.ReceiverBuilder(address, "https://directory.example/", keys);
        using var pinned = builder.WithAmount(amountSats);
        using var bootstrap = pinned.Build();
        var capture = new CapturingReceiverSessionPersister();
        using var initialized = bootstrap.Save(capture);
        var request = CreateRequest(DateTimeOffset.UtcNow.AddHours(1));
        return store.GetOrCreateSession(InvoiceId, address, request.StoreId, request.ExpiresAt!.Value, capture.Events, request);
    }

    private static CreatePayjoinAccountingBridgeRequest CreateRequest(DateTimeOffset expiresAt) =>
        new(InvoiceId, "store-1", PayjoinConstants.BitcoinCode, "BTC-BTC", expiresAt, EffectiveInvoiceValueSats: 1000);
}
