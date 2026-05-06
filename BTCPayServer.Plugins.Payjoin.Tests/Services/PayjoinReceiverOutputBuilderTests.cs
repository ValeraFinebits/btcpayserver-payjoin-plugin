using BTCPayServer.Plugins.Payjoin.Services;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinReceiverOutputBuilderTests
{
    [Fact]
    public void CreateExactPaymentOutputsCreatesDedicatedChangeOutput()
    {
        // Arrange
        var receiverScript = new byte[] { 0x01, 0x02 };
        var receiverChangeScript = new byte[] { 0xAA, 0xBB };

        // Act
        var result = PayjoinReceiverOutputBuilder.CreateExactPaymentOutputs(
            50_000UL,
            receiverScript,
            receiverChangeScript);

        // Assert
        Assert.Equal(receiverChangeScript, result.ReceiverChangeScript);
        Assert.Equal(2, result.ExactPaymentOutputs.Length);
        Assert.Equal<ulong>(50_000UL, result.ExactPaymentOutputs[0].valueSat);
        Assert.Equal(receiverScript, result.ExactPaymentOutputs[0].scriptPubkey);
        Assert.Equal<ulong>(0UL, result.ExactPaymentOutputs[1].valueSat);
        Assert.Equal(receiverChangeScript, result.ExactPaymentOutputs[1].scriptPubkey);
    }
}
