using BTCPayServer.Plugins.Payjoin.Services;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinReceiverProposalFinalizerTests
{
    [Fact]
    public void SigningProcessPsbtReturnsStoredPsbt()
    {
        var processor = new PayjoinReceiverProposalFinalizer.SigningProcessPsbt("stored-psbt");

        var result = processor.Callback("ignored");

        Assert.Equal("stored-psbt", result);
    }
}
