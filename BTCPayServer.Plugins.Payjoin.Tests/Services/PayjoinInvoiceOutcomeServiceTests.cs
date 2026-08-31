using BTCPayServer.Models.InvoicingModels;
using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinInvoiceOutcomeServiceTests
{
    [Fact]
    public void ResolveInvoiceIdPrefersTheModelInvoiceId()
    {
        Assert.Equal("invoice-1", PayjoinInvoiceOutcomeService.ResolveInvoiceId("invoice-1", "invoice-2"));
    }

    [Fact]
    public void ResolveInvoiceIdFallsBackToTheInvoiceEntityForTheInvoiceList()
    {
        Assert.Equal("invoice-2", PayjoinInvoiceOutcomeService.ResolveInvoiceId(null, "invoice-2"));
        Assert.Equal("invoice-2", PayjoinInvoiceOutcomeService.ResolveInvoiceId("", "invoice-2"));
    }

    [Fact]
    public void ResolveInvoiceIdReturnsNullWhenNeitherIsAvailable()
    {
        Assert.Null(PayjoinInvoiceOutcomeService.ResolveInvoiceId(null, null));
        Assert.Null(PayjoinInvoiceOutcomeService.ResolveInvoiceId("", ""));
    }

    [Fact]
    public async Task TryGetAsyncReportsTheBridgeStatusVerbatim()
    {
        foreach (var status in Enum.GetValues<PayjoinAccountingBridgeStatus>())
        {
            var bridgeService = new RecordingBridgeService(CreateBridge(status));
            var service = CreateService(bridgeService);

            var outcome = await service.TryGetAsync(new InvoiceDetailsModel { Id = "invoice-1" }, TestContext.Current.CancellationToken);

            Assert.NotNull(outcome);
            Assert.Equal(status, outcome.Status);
            Assert.Equal("store-1", outcome.StoreId);
            Assert.Equal("final-transaction-1", outcome.SettlementTransactionId);
            Assert.Equal(["invoice-1"], bridgeService.LookedUpInvoiceIds);
        }
    }

    [Fact]
    public async Task TryGetAsyncReportsNoOutcomeWhenTheInvoiceHasNoBridge()
    {
        var bridgeService = new RecordingBridgeService(bridge: null);
        var service = CreateService(bridgeService);

        var outcome = await service.TryGetAsync(new InvoiceDetailsModel { Id = "invoice-1" }, TestContext.Current.CancellationToken);

        Assert.Null(outcome);
        Assert.Equal(["invoice-1"], bridgeService.LookedUpInvoiceIds);
    }

    [Fact]
    public async Task TryGetAsyncDoesNotQueryWhenTheInvoiceIdIsUnknown()
    {
        var bridgeService = new RecordingBridgeService(CreateBridge(PayjoinAccountingBridgeStatus.Reconciled));
        var service = CreateService(bridgeService);

        Assert.Null(await service.TryGetAsync(new InvoiceDetailsModel(), TestContext.Current.CancellationToken));
        Assert.Null(await service.TryGetAsync(null, TestContext.Current.CancellationToken));

        Assert.Empty(bridgeService.LookedUpInvoiceIds);
    }

    [Fact]
    public async Task TryGetAsyncReportsNoOutcomeWhenTheLookupFails()
    {
        var service = CreateService(new ThrowingBridgeService());

        var outcome = await service.TryGetAsync(new InvoiceDetailsModel { Id = "invoice-1" }, TestContext.Current.CancellationToken);

        Assert.Null(outcome);
    }

    private static PayjoinInvoiceOutcomeService CreateService(IPayjoinAccountingBridgeService bridgeService)
    {
        return new PayjoinInvoiceOutcomeService(bridgeService, NullLogger<PayjoinInvoiceOutcomeService>.Instance);
    }

    private static PayjoinAccountingBridgeState CreateBridge(PayjoinAccountingBridgeStatus status)
    {
        return new PayjoinAccountingBridgeState(
            Id: 1,
            InvoiceId: "invoice-1",
            StoreId: "store-1",
            CryptoCode: PayjoinConstants.BitcoinCode,
            PaymentMethodId: "BTC-CHAIN",
            FallbackTransactionId: "fallback-transaction-1",
            FallbackOutputIndex: 0,
            FallbackValueSats: 1000,
            EffectiveInvoiceValueSats: 1000,
            SettlementScript: null,
            ExpectedFinalTransactionId: "final-transaction-1",
            ExpectedFinalOutputIndex: 1,
            ExpectedFinalValueSats: 1000,
            FailureMessage: status == PayjoinAccountingBridgeStatus.Failed ? "boom" : null,
            Status: status,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            ReconciledAt: status == PayjoinAccountingBridgeStatus.Reconciled ? DateTimeOffset.UtcNow : null,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5));
    }

    private sealed class RecordingBridgeService(PayjoinAccountingBridgeState? bridge) : UnusedBridgeService
    {
        public List<string> LookedUpInvoiceIds { get; } = [];

        public override Task<PayjoinAccountingBridgeState?> TryGetByInvoiceIdAsync(string invoiceId, CancellationToken cancellationToken)
        {
            LookedUpInvoiceIds.Add(invoiceId);
            return Task.FromResult(bridge);
        }
    }

    private sealed class ThrowingBridgeService : UnusedBridgeService
    {
        public override Task<PayjoinAccountingBridgeState?> TryGetByInvoiceIdAsync(string invoiceId, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("the plugin database is unreachable");
        }
    }

    private abstract class UnusedBridgeService : IPayjoinAccountingBridgeService
    {
        public abstract Task<PayjoinAccountingBridgeState?> TryGetByInvoiceIdAsync(string invoiceId, CancellationToken cancellationToken);

        public Task<PayjoinAccountingBridgeState> CreateOrGetAsync(CreatePayjoinAccountingBridgeRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<PayjoinAccountingBridgeState>> GetPendingAsync(DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PayjoinAccountingBridgeState?> AttachFallbackAsync(string invoiceId, string fallbackTransactionId, long fallbackOutputIndex, long fallbackValueSats, long effectiveInvoiceValueSats, string? settlementScript, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PayjoinAccountingBridgeState?> SetExpectedFinalTransactionAsync(string invoiceId, string expectedFinalTransactionId, long? expectedFinalOutputIndex, long? expectedFinalValueSats, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PayjoinAccountingBridgeState?> MarkReconciledAsync(string invoiceId, string? expectedFinalTransactionId, long? expectedFinalOutputIndex, long? expectedFinalValueSats, DateTimeOffset reconciledAt, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PayjoinAccountingBridgeState?> MarkFailedAsync(string invoiceId, string failureMessage, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<PayjoinAccountingBridgeState>> ExpirePendingAsync(DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PayjoinAccountingBridgeAttentionResult> GetRequiringAttentionAsync(string storeId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PayjoinAccountingBridgeState?> TryRetryAsync(string invoiceId, string storeId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PayjoinAccountingBridgeState?> ResetForNewSessionAsync(string invoiceId, long? effectiveInvoiceValueSats, DateTimeOffset? expiresAt, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
