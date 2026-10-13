using BTCPayServer.Models.InvoicingModels;
using BTCPayServer.Plugins.Payjoin.Services;
using NBitcoin;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class PayjoinCheckoutModelAssemblyTests
{
    private const string InvoiceId = "invoice-checkout";
    private const string PaymentUrlEndpoint = "/plugins/payjoin/invoices/invoice-checkout/payment-url";
    private const decimal DueBtc = 0.1m;

    private static readonly string ReceiverAddress = CreateRegtestAddress();
    private static readonly string Destination = ReceiverAddress;
    private static readonly string PayjoinUri = $"bitcoin:{ReceiverAddress}?amount=0.1&pjos=0&pj=https%3A%2F%2Fexample.com%2Fpj";
    private static readonly string PlainUrl = $"bitcoin:{ReceiverAddress}?amount=0.10000000&pj=http://localhost/BTC/pj&pjos=1&ark=Mixed%2fCase&lightning=lnbcrt123&label=Store%20Name&message=Order%2f123&pop=https%3a%2f%2fexample.com%2fproof&custom=Keep%2fCase";

    private static string CreateRegtestAddress()
    {
        using var key = new Key();
        return key.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest).ToString();
    }

    private static CheckoutModel CreateModel() => new()
    {
        InvoiceBitcoinUrl = PlainUrl,
        InvoiceBitcoinUrlQR = PlainUrl.ToUpperInvariant()
    };

    private static bool HasPayjoinUrl(CheckoutModel model) =>
        model.AdditionalData.ContainsKey(PayjoinBitcoinCheckoutModelExtension.PayjoinBitcoinUrlKey);

    private static PayjoinSessionUriReader CreateReaderWithServableSession(SessionStoreFixture fixture)
    {
        var store = fixture.CreateStore();
        store.GetOrCreateSession(InvoiceId, Destination, "store-1", DateTimeOffset.UtcNow.AddMinutes(15), ["bootstrap-event"]);
        store.StorePayjoinUri(InvoiceId, Destination, PayjoinUri);
        return new PayjoinSessionUriReader(fixture.CreateStore());
    }

    [Fact]
    public void ServableSessionPublishesThePayjoinUrl()
    {
        using var fixture = new SessionStoreFixture();
        var model = CreateModel();

        PayjoinBitcoinCheckoutModelExtension.ApplyPayjoinCheckoutModel(
            model, true, PaymentUrlEndpoint, InvoiceId, Destination, DueBtc, Network.RegTest, CreateReaderWithServableSession(fixture));

        Assert.Equal(
            PayjoinUri,
            model.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PayjoinBitcoinUrlKey].ToObject<string>());
        Assert.Equal(PayjoinUri, model.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PayjoinBitcoinUrlQrKey].Value<string>());
        Assert.Equal(PlainUrl, model.InvoiceBitcoinUrl);
        Assert.Equal(PlainUrl, model.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PlainBitcoinUrlKey].Value<string>());
        Assert.Equal(PlainUrl, model.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PlainBitcoinUrlQrKey].Value<string>());
        Assert.True(model.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PayjoinV2EnabledKey].ToObject<bool>());
    }

    [Fact]
    public void StoreWithPayjoinDisabledPublishesNoPayjoinUrlEvenWithALiveSession()
    {
        using var fixture = new SessionStoreFixture();
        var model = CreateModel();

        PayjoinBitcoinCheckoutModelExtension.ApplyPayjoinCheckoutModel(
            model, false, PaymentUrlEndpoint, InvoiceId, Destination, DueBtc, Network.RegTest, CreateReaderWithServableSession(fixture));

        Assert.False(HasPayjoinUrl(model));
        Assert.False(model.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PayjoinV2EnabledKey].ToObject<bool>());
        Assert.Equal(
            PlainUrl,
            model.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PlainBitcoinUrlKey].ToObject<string>());
    }

    [Fact]
    public void MissingEndpointPublishesNothingAtAll()
    {
        using var fixture = new SessionStoreFixture();
        var model = CreateModel();

        PayjoinBitcoinCheckoutModelExtension.ApplyPayjoinCheckoutModel(
            model, true, null, InvoiceId, Destination, DueBtc, Network.RegTest, CreateReaderWithServableSession(fixture));

        Assert.Empty(model.AdditionalData);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvoiceWithNothingLeftToPayPublishesNoPayjoinUrl(decimal due)
    {
        using var fixture = new SessionStoreFixture();
        var model = CreateModel();

        PayjoinBitcoinCheckoutModelExtension.ApplyPayjoinCheckoutModel(
            model, true, PaymentUrlEndpoint, InvoiceId, Destination, due, Network.RegTest, CreateReaderWithServableSession(fixture));

        Assert.False(HasPayjoinUrl(model));
    }

    [Fact]
    public void PromptWithoutADestinationPublishesNoPayjoinUrl()
    {
        using var fixture = new SessionStoreFixture();
        var model = CreateModel();

        PayjoinBitcoinCheckoutModelExtension.ApplyPayjoinCheckoutModel(
            model, true, PaymentUrlEndpoint, InvoiceId, null, DueBtc, Network.RegTest, CreateReaderWithServableSession(fixture));

        Assert.False(HasPayjoinUrl(model));
    }

    [Fact]
    public void PromptWithoutADueAmountPublishesNoPayjoinUrl()
    {
        using var fixture = new SessionStoreFixture();
        var model = CreateModel();

        PayjoinBitcoinCheckoutModelExtension.ApplyPayjoinCheckoutModel(
            model, true, PaymentUrlEndpoint, InvoiceId, Destination, null, Network.RegTest, CreateReaderWithServableSession(fixture));

        Assert.False(HasPayjoinUrl(model));
    }

    [Fact]
    public void InvoiceWithoutASessionPublishesTheMetadataButNoPayjoinUrl()
    {
        using var fixture = new SessionStoreFixture();
        var model = CreateModel();

        PayjoinBitcoinCheckoutModelExtension.ApplyPayjoinCheckoutModel(
            model, true, PaymentUrlEndpoint, InvoiceId, Destination, DueBtc, Network.RegTest, new PayjoinSessionUriReader(fixture.CreateStore()));

        Assert.False(HasPayjoinUrl(model));
        Assert.Equal(
            PaymentUrlEndpoint,
            model.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PayjoinPaymentUrlEndpointKey].ToObject<string>());
    }

    [Fact]
    public void SessionBuiltForTheFullPriceDoesNotServeAPartlyPaidInvoice()
    {
        using var fixture = new SessionStoreFixture();
        var model = CreateModel();
        model.InvoiceBitcoinUrl = $"bitcoin:{ReceiverAddress}?amount=0.06000000&lightning=lnbcrt123";

        PayjoinBitcoinCheckoutModelExtension.ApplyPayjoinCheckoutModel(
            model, true, PaymentUrlEndpoint, InvoiceId, Destination, 0.06m, Network.RegTest, CreateReaderWithServableSession(fixture));

        Assert.False(HasPayjoinUrl(model));
    }

    [Fact]
    public void SessionUriWithoutAnEndpointPublishesNoPayjoinUrl()
    {
        using var fixture = new SessionStoreFixture();
        var store = fixture.CreateStore();
        store.GetOrCreateSession(InvoiceId, Destination, "store-1", DateTimeOffset.UtcNow.AddMinutes(15), ["bootstrap-event"]);
        store.StorePayjoinUri(InvoiceId, Destination, $"bitcoin:{ReceiverAddress}?amount=0.1&pjos=0");
        var model = CreateModel();

        PayjoinBitcoinCheckoutModelExtension.ApplyPayjoinCheckoutModel(
            model, true, PaymentUrlEndpoint, InvoiceId, Destination, DueBtc, Network.RegTest, new PayjoinSessionUriReader(fixture.CreateStore()));

        Assert.False(HasPayjoinUrl(model));
    }

    [Fact]
    public void InvalidBaseAddressPublishesNoPayjoinUrl()
    {
        using var fixture = new SessionStoreFixture();
        var model = new CheckoutModel
        {
            InvoiceBitcoinUrl = "bitcoin:not-an-address?amount=0.10000000&lightning=lnbcrt123",
            InvoiceBitcoinUrlQR = "bitcoin:NOT-AN-ADDRESS?amount=0.10000000&lightning=LNBCRT123"
        };

        PayjoinBitcoinCheckoutModelExtension.ApplyPayjoinCheckoutModel(
            model, true, PaymentUrlEndpoint, InvoiceId, Destination, DueBtc, Network.RegTest, CreateReaderWithServableSession(fixture));

        Assert.False(HasPayjoinUrl(model));
    }

    [Fact]
    public void EmptyPlainUrlPublishesNoPayjoinUrl()
    {
        using var fixture = new SessionStoreFixture();
        var model = new CheckoutModel { InvoiceBitcoinUrl = string.Empty, InvoiceBitcoinUrlQR = string.Empty };

        PayjoinBitcoinCheckoutModelExtension.ApplyPayjoinCheckoutModel(
            model, true, PaymentUrlEndpoint, InvoiceId, Destination, DueBtc, Network.RegTest, CreateReaderWithServableSession(fixture));

        Assert.False(HasPayjoinUrl(model));
    }

    [Fact]
    public void SessionBuiltForADifferentAddressPublishesNoPayjoinUrl()
    {
        using var fixture = new SessionStoreFixture();
        var model = CreateModel();

        PayjoinBitcoinCheckoutModelExtension.ApplyPayjoinCheckoutModel(
            model, true, PaymentUrlEndpoint, InvoiceId, "bcrt1qsomewhereelse", DueBtc, Network.RegTest, CreateReaderWithServableSession(fixture));

        Assert.False(HasPayjoinUrl(model));
    }

    [Theory]
    [InlineData("amount=0.2")]
    [InlineData("amount=0.1&amount=0.1")]
    [InlineData("amount=0.1&req-unknown=1")]
    [InlineData("amount=0.1#fragment")]
    public void IncompatibleUnifiedPaymentNeitherBootstrapsNorPublishesASession(string query)
    {
        using var fixture = new SessionStoreFixture();
        var model = CreateModel();
        model.InvoiceBitcoinUrl = $"bitcoin:{ReceiverAddress}?{query}";
        PayjoinBitcoinCheckoutModelExtension.ApplyPayjoinCheckoutModel(
            model, true, PaymentUrlEndpoint, InvoiceId, Destination, DueBtc, Network.RegTest, CreateReaderWithServableSession(fixture));
        Assert.False(HasPayjoinUrl(model));
        Assert.False(model.AdditionalData[PayjoinBitcoinCheckoutModelExtension.CanBootstrapKey].Value<bool>());
    }
}
