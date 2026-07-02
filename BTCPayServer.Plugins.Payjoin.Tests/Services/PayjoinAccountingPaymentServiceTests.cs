using BTCPayServer.Plugins.Payjoin.Services;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinAccountingPaymentServiceTests
{
    [Fact]
    public void ResolveFinalOutputIndexFallsBackToSettlementScriptWhenExpectedFinalOutputIndexMissing()
    {
        // Arrange
        var finalTransaction = Network.RegTest.CreateTransaction();
        using var receiverKey = new Key();
        using var settlementKey = new Key();
        finalTransaction.Outputs.Add(Money.Satoshis(10_000), receiverKey.PubKey.WitHash.ScriptPubKey);
        var settlementScript = settlementKey.PubKey.WitHash.ScriptPubKey;
        finalTransaction.Outputs.Add(Money.Satoshis(20_000), settlementScript);

        var bridge = CreateBridge(settlementScript: Convert.ToHexString(settlementScript.ToBytes()), expectedFinalOutputIndex: null);

        // Act
        var outputIndex = InvokeResolveFinalOutputIndex(finalTransaction, bridge);

        // Assert
        Assert.Equal(1U, outputIndex);
    }

    [Fact]
    public void ResolveFinalOutputIndexReturnsNullWhenSettlementScriptOutputIsMissing()
    {
        // Arrange
        var finalTransaction = Network.RegTest.CreateTransaction();
        using var receiverKey = new Key();
        using var settlementKey = new Key();
        finalTransaction.Outputs.Add(Money.Satoshis(10_000), receiverKey.PubKey.WitHash.ScriptPubKey);

        var settlementScript = settlementKey.PubKey.WitHash.ScriptPubKey;
        var bridge = CreateBridge(settlementScript: Convert.ToHexString(settlementScript.ToBytes()), expectedFinalOutputIndex: null);

        // Act
        var outputIndex = InvokeResolveFinalOutputIndex(finalTransaction, bridge);

        // Assert
        Assert.Null(outputIndex);
    }

    [Theory]
    [InlineData(false, true, 0L, true)]
    [InlineData(false, true, 1L, false)]
    [InlineData(false, false, 0L, false)]
    [InlineData(true, true, 0L, false)]
    [InlineData(true, false, 5L, false)]
    public void ShouldWaitForFinalTransactionConfirmationDefersOnlyWhileAnUnconfirmedFallbackPaymentIsAccounted(
        bool finalPaymentExists,
        bool trackedPaymentExists,
        long confirmations,
        bool expected)
    {
        var shouldWait = PayjoinAccountingPaymentService.ShouldWaitForFinalTransactionConfirmation(finalPaymentExists, trackedPaymentExists, confirmations);

        Assert.Equal(expected, shouldWait);
    }

    [Fact]
    public void ResolveTrackedPaymentIdReturnsNullWhenFallbackOutPointIsMissing()
    {
        var bridge = CreateBridge(settlementScript: null, expectedFinalOutputIndex: null);

        var trackedPaymentId = PayjoinAccountingPaymentService.ResolveTrackedPaymentId(bridge);

        Assert.Null(trackedPaymentId);
    }

    [Fact]
    public void ResolveTrackedPaymentIdReturnsFallbackOutPointWhenPresent()
    {
        var bridge = CreateBridge(
            settlementScript: null,
            expectedFinalOutputIndex: null,
            fallbackTransactionId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            fallbackOutputIndex: 3);

        var trackedPaymentId = PayjoinAccountingPaymentService.ResolveTrackedPaymentId(bridge);

        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-3", trackedPaymentId);
    }

    [Fact]
    public void ResolveFinalOutputIndexThrowsWhenSettlementScriptMatchesMultipleOutputs()
    {
        // Arrange
        var finalTransaction = Network.RegTest.CreateTransaction();
        using var settlementKey = new Key();
        var settlementScript = settlementKey.PubKey.WitHash.ScriptPubKey;
        finalTransaction.Outputs.Add(Money.Satoshis(10_000), settlementScript);
        finalTransaction.Outputs.Add(Money.Satoshis(20_000), settlementScript);

        var bridge = CreateBridge(settlementScript: Convert.ToHexString(settlementScript.ToBytes()), expectedFinalOutputIndex: null);

        // Act + Assert
        var ex = Assert.Throws<PayjoinAccountingReconciliationDataException>(() => InvokeResolveFinalOutputIndex(finalTransaction, bridge));
        Assert.Contains("Ambiguous settlement script persisted", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveFinalOutputIndexFallsBackToSettlementScriptWhenExpectedFinalOutputIndexPointsToDifferentScript()
    {
        // Arrange
        var finalTransaction = Network.RegTest.CreateTransaction();
        using var wrongKey = new Key();
        using var settlementKey = new Key();
        finalTransaction.Outputs.Add(Money.Satoshis(10_000), wrongKey.PubKey.WitHash.ScriptPubKey);
        var settlementScript = settlementKey.PubKey.WitHash.ScriptPubKey;
        finalTransaction.Outputs.Add(Money.Satoshis(20_000), settlementScript);

        var bridge = CreateBridge(settlementScript: Convert.ToHexString(settlementScript.ToBytes()), expectedFinalOutputIndex: 0);

        // Act
        var outputIndex = InvokeResolveFinalOutputIndex(finalTransaction, bridge);

        // Assert
        Assert.Equal(1U, outputIndex);
    }

    [Fact]
    public void ResolveFinalOutputIndexFallsBackToSettlementScriptWhenExpectedFinalOutputIndexIsOutOfRange()
    {
        // Arrange
        var finalTransaction = Network.RegTest.CreateTransaction();
        using var receiverKey = new Key();
        using var settlementKey = new Key();
        finalTransaction.Outputs.Add(Money.Satoshis(10_000), receiverKey.PubKey.WitHash.ScriptPubKey);
        var settlementScript = settlementKey.PubKey.WitHash.ScriptPubKey;
        finalTransaction.Outputs.Add(Money.Satoshis(20_000), settlementScript);

        var bridge = CreateBridge(settlementScript: Convert.ToHexString(settlementScript.ToBytes()), expectedFinalOutputIndex: 99);

        // Act
        var outputIndex = InvokeResolveFinalOutputIndex(finalTransaction, bridge);

        // Assert
        Assert.Equal(1U, outputIndex);
    }

    [Fact]
    public void ResolveFinalOutputIndexReturnsNullWhenExpectedFinalOutputIndexIsOutOfRangeAndSettlementScriptDoesNotMatch()
    {
        // Arrange
        var finalTransaction = Network.RegTest.CreateTransaction();
        using var receiverKey = new Key();
        using var settlementKey = new Key();
        finalTransaction.Outputs.Add(Money.Satoshis(10_000), receiverKey.PubKey.WitHash.ScriptPubKey);

        var settlementScript = settlementKey.PubKey.WitHash.ScriptPubKey;
        var bridge = CreateBridge(settlementScript: Convert.ToHexString(settlementScript.ToBytes()), expectedFinalOutputIndex: 99);

        // Act
        var outputIndex = InvokeResolveFinalOutputIndex(finalTransaction, bridge);

        // Assert
        Assert.Null(outputIndex);
    }

    private static uint? InvokeResolveFinalOutputIndex(Transaction finalTransaction, PayjoinAccountingBridgeState bridge)
    {
        return PayjoinAccountingPaymentService.ResolveFinalOutputIndex(finalTransaction, bridge);
    }

    private static PayjoinAccountingBridgeState CreateBridge(
        string? settlementScript,
        long? expectedFinalOutputIndex,
        string? fallbackTransactionId = null,
        long? fallbackOutputIndex = null)
    {
        return new PayjoinAccountingBridgeState(
            Id: 1,
            InvoiceId: "invoice-1",
            StoreId: "store-1",
            CryptoCode: PayjoinConstants.BitcoinCode,
            PaymentMethodId: "BTC-BTC",
            FallbackTransactionId: fallbackTransactionId,
            FallbackOutputIndex: fallbackOutputIndex,
            FallbackValueSats: 1000,
            EffectiveInvoiceValueSats: 1000,
            SettlementScript: settlementScript,
            ExpectedFinalTransactionId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            ExpectedFinalOutputIndex: expectedFinalOutputIndex,
            ExpectedFinalValueSats: 1000,
            FailureMessage: null,
            Status: Data.PayjoinAccountingBridgeStatus.PendingFinalTransaction,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            ReconciledAt: null,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5));
    }
}
