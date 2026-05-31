using BTCPayServer.Plugins.Payjoin.Services;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinReceiverOutputBuilderTests
{
    [Fact]
    public void CreateSettlementOutputsCreatesSingleSettlementOutput()
    {
        // Arrange
        var settlementScript = new byte[] { 0xAA, 0xBB };

        // Act
        var result = PayjoinReceiverOutputBuilder.CreateSettlementOutputs(50_000UL, settlementScript);

        // Assert
        Assert.Equal(settlementScript, result.SettlementScript);
        Assert.Single(result.ReplacementOutputs);
        Assert.Equal<ulong>(50_000UL, result.ReplacementOutputs[0].valueSat);
        Assert.Equal(settlementScript, result.ReplacementOutputs[0].scriptPubkey);
    }
}
