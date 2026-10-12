using BTCPayServer.Plugins.Payjoin.Services;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class PayjoinSessionUriReaderFailureTests
{
    [Fact]
    public void DatabaseFailureRendersPlainBip21RatherThanThrowing()
    {
        using var testContext = new RelationalPluginTestContext();
        var reader = new PayjoinSessionUriReader(testContext.CreateStore());
        testContext.BreakDatabase();

        Assert.Null(reader.TryGetExistingPayjoinUri("invoice-broken-db", TestSessionStates.DefaultReceiverAddress));
    }

    [Fact]
    public void DatabaseFailureLeavesTheCheckoutModelUsable()
    {
        using var testContext = new RelationalPluginTestContext();
        var reader = new PayjoinSessionUriReader(testContext.CreateStore());
        testContext.BreakDatabase();
        var model = new BTCPayServer.Models.InvoicingModels.CheckoutModel
        {
            InvoiceBitcoinUrl = "bitcoin:bcrt1qexample?amount=0.10000000",
            InvoiceBitcoinUrlQR = "bitcoin:BCRT1QEXAMPLE?amount=0.10000000"
        };

        PayjoinBitcoinCheckoutModelExtension.ApplyPayjoinCheckoutModel(
            model, true, "/plugins/payjoin/invoices/x/payment-url", "invoice-broken-db",
            TestSessionStates.DefaultReceiverAddress, 0.1m, reader);

        Assert.False(model.AdditionalData.ContainsKey(PayjoinBitcoinCheckoutModelExtension.PayjoinBitcoinUrlKey));
        Assert.Equal(
            "bitcoin:bcrt1qexample?amount=0.10000000",
            model.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PlainBitcoinUrlKey].ToObject<string>());
    }
}
