using BTCPayServer.Models.InvoicingModels;
using BTCPayServer.Plugins.Payjoin.Services;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class PayjoinBitcoinCheckoutModelExtensionTests
{
    [Fact]
    public void ApplyPayjoinPaymentUrlKeepsPlainAndPayjoinUrlsForCheckoutToggle()
    {
        using var key = new Key();
        var address = key.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest).ToString();
        var upperAddress = address.ToUpperInvariant();
        var model = new CheckoutModel
        {
            InvoiceBitcoinUrl = $"bitcoin:{address}?amount=0.10000000&lightning=lnbcrt123",
            InvoiceBitcoinUrlQR = $"bitcoin:{upperAddress}?amount=0.10000000&lightning=LNBCRT123"
        };

        PayjoinBitcoinCheckoutModelExtension.ApplyPayjoinCheckoutMetadata(model, "/plugins/payjoin/invoices/test/payment-url", true);
        PayjoinBitcoinCheckoutModelExtension.ApplyPayjoinPaymentUrl(
            model,
            $"bitcoin:{address}?amount=0.1&pjos=0&pj=https%3A%2F%2Fexample.com%2Fpj");

        Assert.Equal($"bitcoin:{address}?amount=0.10000000&lightning=lnbcrt123", model.InvoiceBitcoinUrl);
        Assert.Equal($"bitcoin:{upperAddress}?amount=0.10000000&lightning=LNBCRT123", model.InvoiceBitcoinUrlQR);
        Assert.Equal(
            $"bitcoin:{address}?amount=0.10000000&lightning=lnbcrt123",
            model.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PlainBitcoinUrlKey].ToObject<string>());
        Assert.Equal(
            $"bitcoin:{address}?amount=0.10000000&pjos=0&pj=https%3A%2F%2Fexample.com%2Fpj&lightning=lnbcrt123",
            model.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PayjoinBitcoinUrlKey].ToObject<string>());
        Assert.Equal(
            $"bitcoin:{upperAddress}?amount=0.10000000&pjos=0&pj=https%3A%2F%2Fexample.com%2Fpj&lightning=LNBCRT123",
            model.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PayjoinBitcoinUrlQrKey].ToObject<string>());
        Assert.Equal(
            $"bitcoin:{upperAddress}?amount=0.10000000&lightning=LNBCRT123",
            model.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PlainBitcoinUrlQrKey].ToObject<string>());
        Assert.True(model.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PayjoinV2EnabledKey].ToObject<bool>());
    }

    [Fact]
    public void ApplyPayjoinPaymentUrlPublishesNeitherUrlWhenTheQrVariantIsNotServable()
    {
        using var key = new Key();
        var address = key.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest).ToString();
        var model = new CheckoutModel
        {
            InvoiceBitcoinUrl = $"bitcoin:{address}?amount=0.10000000",
            InvoiceBitcoinUrlQR = "bitcoin:not-a-valid-address?amount=0.10000000"
        };

        PayjoinBitcoinCheckoutModelExtension.ApplyPayjoinPaymentUrl(
            model,
            $"bitcoin:{address}?amount=0.1&pj=https%3A%2F%2Fexample.com%2Fpj");

        Assert.False(model.AdditionalData.ContainsKey(PayjoinBitcoinCheckoutModelExtension.PayjoinBitcoinUrlKey));
        Assert.False(model.AdditionalData.ContainsKey(PayjoinBitcoinCheckoutModelExtension.PayjoinBitcoinUrlQrKey));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ApplyPayjoinCheckoutMetadataReportsFailureWithoutAnEndpoint(string? endpoint)
    {
        var model = new CheckoutModel
        {
            InvoiceBitcoinUrl = "bitcoin:bcrt1qexample?amount=0.10000000",
            InvoiceBitcoinUrlQR = "bitcoin:BCRT1QEXAMPLE?amount=0.10000000"
        };

        Assert.False(PayjoinBitcoinCheckoutModelExtension.ApplyPayjoinCheckoutMetadata(model, endpoint, true));
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
        Assert.False(model.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PayjoinV2EnabledKey].ToObject<bool>());
    }
}
