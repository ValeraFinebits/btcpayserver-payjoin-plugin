using BTCPayServer.Data;
using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

/// <summary>
/// Lifecycle tests across history retention and the accounting worker, driving the real bridge
/// service and the real poller reconciliation pass rather than the field reset in isolation.
/// </summary>
public class PayjoinAccountingBridgeLifecycleFlowTests
{
    private const string OldSessionFinalTransactionId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string FallbackTransactionId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignedHistoryIsHeldUntilAccountingSucceedsOrFails(bool failAccounting)
    {
        // A signed proposal may still settle after checkout closes. Keep its original
        // history through the accounting pass, including a terminal reconciliation failure.
        using var testContext = new RelationalPluginTestContext();
        var bridgeService = testContext.CreateBridgeService();
        var store = testContext.CreateStore();
        var now = DateTimeOffset.UtcNow;
        var session = store.GetOrCreateSession("invoice-lifecycle", "receiver", "store-1", now.AddMinutes(1), ["original", "signed proposal"]);
        store.StorePayjoinUri(session.InvoiceId, session.ReceiverAddress, "bitcoin:receiver?amount=0.000009&pj=https://example.com/exact");

        await testContext.SeedBridgeAsync(
            new CreatePayjoinAccountingBridgeRequest(
                "invoice-lifecycle",
                "store-1",
                PayjoinConstants.BitcoinCode,
                "BTC-BTC",
                now.AddHours(1),
                EffectiveInvoiceValueSats: 900, FallbackTransactionId: FallbackTransactionId, FallbackOutputIndex: 0, FallbackValueSats: 900,
                SettlementScript: "CCDD", ExpectedFinalTransactionId: OldSessionFinalTransactionId, ExpectedFinalOutputIndex: 0, ExpectedFinalValueSats: 950),
            CancellationToken.None);
        Assert.False(store.RemoveSession(session.InvoiceId));
        Assert.False(store.TryRemoveSessionUnlessNegotiating(session.InvoiceId));

        // The old final transaction becomes observable: the worker's reconciliation pass runs and
        // the payment settles.
        using var poller = new PayjoinReceiverPoller(
            testContext.CreateStore(),
            new NoOpSessionProcessor(),
            bridgeService,
            new SettledPaymentAccountingPaymentService(failAccounting),
            NullLogger<PayjoinReceiverPoller>.Instance);
        await poller.ProcessTickOnceAsync(CancellationToken.None);

        var reconciled = await bridgeService.TryGetByInvoiceIdAsync("invoice-lifecycle", CancellationToken.None);
        Assert.NotNull(reconciled);
        Assert.Equal(failAccounting ? PayjoinAccountingBridgeStatus.Failed : PayjoinAccountingBridgeStatus.Reconciled, reconciled!.Status);
        Assert.Equal(OldSessionFinalTransactionId, reconciled.ExpectedFinalTransactionId);
        Assert.True(store.TryGetSession(session.InvoiceId, out var held));
        Assert.NotNull(held);
        Assert.Equal(session.GetEvents(), held.GetEvents());
        Assert.Equal("bitcoin:receiver?amount=0.000009&pj=https://example.com/exact", held.PayjoinUri);
        Assert.Equal(!failAccounting, store.RemoveSession(session.InvoiceId));
        Assert.Equal(failAccounting, store.TryGetSession(session.InvoiceId, out _));
    }

    private sealed class NoOpSessionProcessor : IPayjoinReceiverSessionProcessor
    {
        public Task ProcessTickAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }

    private sealed class SettledPaymentAccountingPaymentService(bool failAccounting) : IPayjoinAccountingPaymentService
    {
        public Task<BTCPayServer.Services.Invoices.PaymentEntity?> ReconcileWithFinalTransactionAsync(PayjoinAccountingBridgeState bridge, CancellationToken cancellationToken)
        {
            if (failAccounting) throw new PayjoinAccountingReconciliationDataException("settlement mismatch");
            return Task.FromResult<BTCPayServer.Services.Invoices.PaymentEntity?>(new BTCPayServer.Services.Invoices.PaymentEntity
            {
                Status = PaymentStatus.Settled
            });
        }
    }
}
