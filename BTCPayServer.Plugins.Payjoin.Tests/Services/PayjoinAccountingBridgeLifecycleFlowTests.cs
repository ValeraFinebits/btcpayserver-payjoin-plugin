using BTCPayServer.Data;
using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

/// <summary>
/// Lifecycle tests across session recreation and the accounting worker, driving the real bridge
/// service and the real poller reconciliation pass rather than the field reset in isolation.
/// </summary>
public class PayjoinAccountingBridgeLifecycleFlowTests
{
    private const string OldSessionFinalTransactionId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string FallbackTransactionId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task OldSessionsFinalTransactionStillReconcilesAfterSessionRecreation()
    {
        // The lifecycle Valera's review asks about: a previous session already reached
        // PendingFinalTransaction (its signed proposal is in the sender's hands), a fresh receiver
        // session is created for the same invoice and resets the bridge, and the previous session's
        // final transaction becomes observable afterwards. The armed expectation survives the reset
        // by design, and the worker's reconciliation pass still credits the settlement.
        using var testContext = new RelationalPluginTestContext();
        var bridgeService = testContext.CreateBridgeService();
        var now = DateTimeOffset.UtcNow;

        await bridgeService.CreateOrGetAsync(
            new CreatePayjoinAccountingBridgeRequest(
                "invoice-lifecycle",
                "store-1",
                PayjoinConstants.BitcoinCode,
                "BTC-BTC",
                now.AddHours(1),
                EffectiveInvoiceValueSats: 1000),
            CancellationToken.None);
        await bridgeService.AttachFallbackAsync("invoice-lifecycle", FallbackTransactionId, 0, 900, 900, "CCDD", CancellationToken.None);
        await bridgeService.SetExpectedFinalTransactionAsync("invoice-lifecycle", OldSessionFinalTransactionId, 0, 950, CancellationToken.None);

        // Session recreation: the reset runs, and must leave the armed expectation alone.
        var afterReset = await bridgeService.ResetForNewSessionAsync("invoice-lifecycle", 1200, now.AddHours(2), CancellationToken.None);
        Assert.NotNull(afterReset);
        Assert.Equal(OldSessionFinalTransactionId, afterReset!.ExpectedFinalTransactionId);

        // The old final transaction becomes observable: the worker's reconciliation pass runs and
        // the payment settles.
        using var poller = new PayjoinReceiverPoller(
            testContext.CreateStore(),
            new NoOpSessionProcessor(),
            bridgeService,
            new SettledPaymentAccountingPaymentService(),
            NullLogger<PayjoinReceiverPoller>.Instance);
        await poller.ProcessTickOnceAsync(CancellationToken.None);

        var reconciled = await bridgeService.TryGetByInvoiceIdAsync("invoice-lifecycle", CancellationToken.None);
        Assert.NotNull(reconciled);
        Assert.Equal(PayjoinAccountingBridgeStatus.Reconciled, reconciled!.Status);
        Assert.Equal(OldSessionFinalTransactionId, reconciled.ExpectedFinalTransactionId);
    }

    private sealed class NoOpSessionProcessor : IPayjoinReceiverSessionProcessor
    {
        public Task ProcessTickAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }

    private sealed class SettledPaymentAccountingPaymentService : IPayjoinAccountingPaymentService
    {
        public Task<BTCPayServer.Services.Invoices.PaymentEntity?> ReconcileWithFinalTransactionAsync(PayjoinAccountingBridgeState bridge, CancellationToken cancellationToken)
        {
            return Task.FromResult<BTCPayServer.Services.Invoices.PaymentEntity?>(new BTCPayServer.Services.Invoices.PaymentEntity
            {
                Status = PaymentStatus.Settled
            });
        }
    }
}
