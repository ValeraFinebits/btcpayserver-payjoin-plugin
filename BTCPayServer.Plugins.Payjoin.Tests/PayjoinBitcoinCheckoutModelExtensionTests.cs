using BTCPayServer.Models.InvoicingModels;
using BTCPayServer.Plugins.Payjoin.Services;
using NBitcoin;
using Newtonsoft.Json.Linq;
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
        var plainUrl = $"bitcoin:{address}?amount=0.10000000&pj=http://localhost/BTC/pj&pjos=1&ark=Mixed%2fCase&lightning=lnbcrt123&label=Store%20Name&message=Order%2f123&pop=https%3a%2f%2fexample.com%2fproof&custom=Keep%2fCase";
        var payjoinUri = $"bitcoin:{address}?amount=0.1&pjos=0&pj=https%3A%2F%2Fexample.com%2Fpj";
        var model = new CheckoutModel
        {
            InvoiceBitcoinUrl = plainUrl,
            InvoiceBitcoinUrlQR = $"bitcoin:{upperAddress}?amount=0.10000000&lightning=LNBCRT123"
        };

        PayjoinBitcoinCheckoutModelExtension.ApplyPayjoinCheckoutMetadata(model, "/plugins/payjoin/invoices/test/payment-url", true);
        PayjoinBitcoinCheckoutModelExtension.ApplyPayjoinPaymentUrl(
            model,
            payjoinUri);

        Assert.Equal(plainUrl, model.InvoiceBitcoinUrl);
        Assert.Equal($"bitcoin:{upperAddress}?amount=0.10000000&lightning=LNBCRT123", model.InvoiceBitcoinUrlQR);
        Assert.Equal(
            plainUrl,
            model.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PlainBitcoinUrlKey].ToObject<string>());
        Assert.Equal(
            payjoinUri,
            model.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PayjoinBitcoinUrlKey].ToObject<string>());
        Assert.Equal(
            payjoinUri,
            model.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PayjoinBitcoinUrlQrKey].ToObject<string>());
        Assert.Equal(
            plainUrl,
            model.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PlainBitcoinUrlQrKey].ToObject<string>());
        Assert.True(model.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PayjoinV2EnabledKey].ToObject<bool>());
    }

    [Fact]
    public void ApplyPayjoinPaymentUrlPreservesTheEntireV2SessionUri()
    {
        using var key = new Key();
        var address = key.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest).ToString();
        var model = new CheckoutModel
        {
            InvoiceBitcoinUrl = $"bitcoin:{address}?amount=0.10000000&ark=Mixed%2fCase",
            InvoiceBitcoinUrlQR = $"bitcoin:{address.ToUpperInvariant()}?amount=0.10000000&ark=MIXED%2FCASE"
        };

        var payjoinUri = $"bitcoin:{address.ToUpperInvariant()}?amount=0.1&pjos=0&pj=HTTPS%3a%2f%2fEXAMPLE.COM%2fTXJCGKTKXLUUZ%23EX1C4UC6ES-OH1QYPM5JXYNS754Y4R45QWE336QFX6ZR8DQGVQCULVZTV20TFVEYDMFQC-RK1Q0DJS3VVDXWQQTLQ8022QGXSX7ML9PHZ6EDSF6AKEWQG758JPS2EV";
        using var parsed = global::Payjoin.Uri.Parse(payjoinUri);
        using var supported = parsed.CheckPjSupported();
        Assert.Contains("#EX1", supported.PjEndpoint(), StringComparison.Ordinal);

        PayjoinBitcoinCheckoutModelExtension.ApplyPayjoinPaymentUrl(model, payjoinUri);

        Assert.Equal(payjoinUri, model.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PayjoinBitcoinUrlKey].Value<string>());
        Assert.Equal(payjoinUri, model.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PayjoinBitcoinUrlQrKey].Value<string>());
    }

    [Theory]
    [InlineData("pj")]
    [InlineData("pj=")]
    [InlineData("pj=https%3A%2F%2Fexample.com%2Fone&pj=https%3A%2F%2Fexample.com%2Fone")]
    [InlineData("pj=https%3A%2F%2Fexample.com%2Fone&pj=https%3A%2F%2Fexample.com%2Ftwo")]
    [InlineData("pjos=0&pjos=1&pj=https%3A%2F%2Fexample.com%2Fpj")]
    [InlineData("pjos&pj=https%3A%2F%2Fexample.com%2Fpj")]
    [InlineData("pjos=&pj=https%3A%2F%2Fexample.com%2Fpj")]
    public void ApplyPayjoinPaymentUrlPublishesNothingForMalformedOrDuplicateParameters(string query)
    {
        using var key = new Key();
        var address = key.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest).ToString();
        var model = new CheckoutModel
        {
            InvoiceBitcoinUrl = $"bitcoin:{address}?amount=0.10000000",
            InvoiceBitcoinUrlQR = $"bitcoin:{address.ToUpperInvariant()}?amount=0.10000000"
        };

        PayjoinBitcoinCheckoutModelExtension.ApplyPayjoinPaymentUrl(
            model,
            $"bitcoin:{address}?amount=0.1&{query}");

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
