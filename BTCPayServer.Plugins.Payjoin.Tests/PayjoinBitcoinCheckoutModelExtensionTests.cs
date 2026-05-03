using BTCPayServer.Models.InvoicingModels;
using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class PayjoinBitcoinCheckoutModelExtensionTests
{
    [Fact]
    public void MergePayjoinIntoPaymentUrlPreservesExistingQueryParameters()
    {
        const string baseUrl = "bitcoin:bcrt1qexample?amount=0.10000000&lightning=lnbcrt123";
        const string payjoinUrl = "bitcoin:bcrt1qexample?amount=0.10000000&pj=https%3A%2F%2Fexample.com%2Fpj";

        var merged = PayjoinBitcoinCheckoutModelExtension.MergePayjoinIntoPaymentUrl(baseUrl, payjoinUrl);

        Assert.Equal(
            "bitcoin:bcrt1qexample?amount=0.10000000&pj=https%3A%2F%2Fexample.com%2Fpj&lightning=lnbcrt123",
            merged);
    }

    [Fact]
    public void MergePayjoinIntoPaymentUrlPreservesOutputSubstitutionParameter()
    {
        const string baseUrl = "bitcoin:bcrt1qexample?amount=0.10000000";
        const string payjoinUrl = "bitcoin:bcrt1qexample?amount=0.10000000&pjos=0&pj=https%3A%2F%2Fexample.com%2Fpj";

        var merged = PayjoinBitcoinCheckoutModelExtension.MergePayjoinIntoPaymentUrl(baseUrl, payjoinUrl);

        Assert.Equal(
            "bitcoin:bcrt1qexample?amount=0.10000000&pjos=0&pj=https%3A%2F%2Fexample.com%2Fpj",
            merged);
    }

    [Fact]
    public void MergePayjoinIntoPaymentUrlInsertsPayjoinParametersBeforeLightningFallback()
    {
        const string baseUrl = "bitcoin:bcrt1qexample?amount=0.10000000&lightning=lnbcrt123";
        const string payjoinUrl = "bitcoin:bcrt1qexample?amount=0.10000000&pjos=0&pj=https%3A%2F%2Fexample.com%2Fpj";

        var merged = PayjoinBitcoinCheckoutModelExtension.MergePayjoinIntoPaymentUrl(baseUrl, payjoinUrl);

        Assert.Equal(
            "bitcoin:bcrt1qexample?amount=0.10000000&pjos=0&pj=https%3A%2F%2Fexample.com%2Fpj&lightning=lnbcrt123",
            merged);
    }

    [Fact]
    public void MergePayjoinIntoPaymentUrlRemovesStalePayjoinParametersWhenFallingBackToPlainBip21()
    {
        const string baseUrl = "bitcoin:BCRT1QEXAMPLE?amount=0.10000000&lightning=LNBCRT123&pjos=0&pj=https%3A%2F%2Fold.example%2Fpj";
        const string payjoinFallbackUrl = "bitcoin:bcrt1qexample?amount=0.10000000";

        var merged = PayjoinBitcoinCheckoutModelExtension.MergePayjoinIntoPaymentUrl(baseUrl, payjoinFallbackUrl);

        Assert.Equal("bitcoin:BCRT1QEXAMPLE?amount=0.10000000&lightning=LNBCRT123", merged);
    }

    [Fact]
    public void ApplyPayjoinPaymentUrlKeepsPlainAndPayjoinUrlsForCheckoutToggle()
    {
        var model = new CheckoutModel
        {
            InvoiceBitcoinUrl = "bitcoin:bcrt1qexample?amount=0.10000000&lightning=lnbcrt123",
            InvoiceBitcoinUrlQR = "bitcoin:BCRT1QEXAMPLE?amount=0.10000000&lightning=LNBCRT123"
        };
        var paymentUrl = new GetBip21Response
        {
            PayjoinEnabled = true,
            Bip21 = "bitcoin:bcrt1qexample?amount=0.10000000&pjos=0&pj=https%3A%2F%2Fexample.com%2Fpj"
        };

        PayjoinBitcoinCheckoutModelExtension.ApplyPayjoinPaymentUrl(model, paymentUrl, true);

        Assert.Equal(
            "bitcoin:bcrt1qexample?amount=0.10000000&pjos=0&pj=https%3A%2F%2Fexample.com%2Fpj&lightning=lnbcrt123",
            model.InvoiceBitcoinUrl);
        Assert.Equal(
            "bitcoin:BCRT1QEXAMPLE?amount=0.10000000&pjos=0&pj=https%3A%2F%2Fexample.com%2Fpj&lightning=LNBCRT123",
            model.InvoiceBitcoinUrlQR);
        Assert.Equal(
            "bitcoin:bcrt1qexample?amount=0.10000000&lightning=lnbcrt123",
            model.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PlainBitcoinUrlKey].ToObject<string>());
        Assert.Equal(
            "bitcoin:bcrt1qexample?amount=0.10000000&pjos=0&pj=https%3A%2F%2Fexample.com%2Fpj&lightning=lnbcrt123",
            model.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PayjoinBitcoinUrlKey].ToObject<string>());
        Assert.True(model.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PayjoinDefaultEnabledKey].ToObject<bool>());
    }

    [Fact]
    public void ApplyPayjoinPaymentUrlLeavesPlainCheckoutModelWhenPayjoinUnavailable()
    {
        var model = new CheckoutModel
        {
            InvoiceBitcoinUrl = "bitcoin:bcrt1qexample?amount=0.10000000",
            InvoiceBitcoinUrlQR = "bitcoin:BCRT1QEXAMPLE?amount=0.10000000"
        };
        var paymentUrl = new GetBip21Response
        {
            PayjoinEnabled = false,
            Bip21 = "bitcoin:bcrt1qexample?amount=0.10000000"
        };

        PayjoinBitcoinCheckoutModelExtension.ApplyPayjoinPaymentUrl(model, paymentUrl, true);

        Assert.Equal("bitcoin:bcrt1qexample?amount=0.10000000", model.InvoiceBitcoinUrl);
        Assert.Empty(model.AdditionalData);
    }

    [Fact]
    public void ApplyPayjoinCheckoutMetadataReflectsStoreDefaultMode()
    {
        var model = new CheckoutModel
        {
            InvoiceBitcoinUrl = "bitcoin:bcrt1qexample?amount=0.10000000",
            InvoiceBitcoinUrlQR = "bitcoin:BCRT1QEXAMPLE?amount=0.10000000"
        };

        PayjoinBitcoinCheckoutModelExtension.ApplyPayjoinCheckoutMetadata(model, "/plugins/payjoin/invoices/test/payment-url", false);

        Assert.Equal(
            "bitcoin:bcrt1qexample?amount=0.10000000",
            model.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PlainBitcoinUrlKey].ToObject<string>());
        Assert.Equal(
            "/plugins/payjoin/invoices/test/payment-url",
            model.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PayjoinPaymentUrlEndpointKey].ToObject<string>());
        Assert.False(model.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PayjoinDefaultEnabledKey].ToObject<bool>());
    }
}
