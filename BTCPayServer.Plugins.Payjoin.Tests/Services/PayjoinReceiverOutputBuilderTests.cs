using BTCPayServer.Plugins.Payjoin.Services;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinReceiverOutputBuilderTests
{
    [Fact]
    public void CreateSettlementOutputsCreatesSingleSettlementOutput()
    {
        // Arrange
        var settlementScript = new byte[] { 0xAA, 0xBB };
        var settlementKeyPath = new KeyPath("1/18");

        // Act
        var result = PayjoinReceiverOutputBuilder.CreateSettlementOutputs(50_000UL, settlementScript, settlementKeyPath);

        // Assert
        Assert.Equal(settlementScript, result.SettlementScript);
        Assert.Equal(settlementKeyPath, result.SettlementKeyPath);
        Assert.Single(result.ReplacementOutputs);
        Assert.Equal<ulong>(50_000UL, result.ReplacementOutputs[0].ValueSat);
        Assert.Equal(settlementScript, result.ReplacementOutputs[0].ScriptPubkey);
    }

    [Fact]
    public void CreateSettlementOutputsSupportsPreservingReceiverScript()
    {
        var receiverScript = new byte[] { 0x01, 0x02, 0x03 };
        var receiverKeyPath = new KeyPath("0/7");

        var result = PayjoinReceiverOutputBuilder.CreateSettlementOutputs(75_000UL, receiverScript, receiverKeyPath);

        Assert.Equal(receiverScript, result.SettlementScript);
        Assert.Equal(receiverKeyPath, result.SettlementKeyPath);
        Assert.Single(result.ReplacementOutputs);
        Assert.Equal<ulong>(75_000UL, result.ReplacementOutputs[0].ValueSat);
        Assert.Equal(receiverScript, result.ReplacementOutputs[0].ScriptPubkey);
    }
}
