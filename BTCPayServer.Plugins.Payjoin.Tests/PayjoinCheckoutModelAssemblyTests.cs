using BTCPayServer.Models.InvoicingModels;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using Newtonsoft.Json;
using NSubstitute;
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

    [Theory]
    [InlineData(false, "NBXplorerGenerated", true, true, false, false)]
    [InlineData(false, "ImportedSeed", true, true, false, false)]
    [InlineData(true, "SeedImported", true, true, false, false)]
    [InlineData(true, "ImportedSeed", true, true, true, true)]
    [InlineData(true, "NBXplorerGenerated", false, true, true, false)]
    [InlineData(true, "ImportedSeed", false, true, true, false)]
    [InlineData(true, "NBXplorerGenerated", true, true, true, true)]
    [InlineData(true, "NBXplorerGenerated", true, false, false, false)]
    [InlineData(true, "ImportedSeed", true, false, false, false)]
    [InlineData(true, "ImportedSeed", true, true, true, false, "json")]
    [InlineData(true, "ImportedSeed", true, true, true, false, "format")]
    [InlineData(true, "ImportedSeed", true, true, true, false, "cancel")]
    public void RealCheckoutUsesCurrentProfileAndRetainsTheSameSession(
        bool hot, string source, bool accountAvailable, bool enabled, bool bootstrap, bool publish, string? failure = null)
    {
        using var fixture = new SessionStoreFixture();
        var reader = CreateReaderWithServableSession(fixture);
        var sessions = fixture.CreateStore();
        Assert.True(sessions.TryGetSession(InvoiceId, out var before));
        var events = before!.GetEvents();
        var nbx = new NBXplorerNetworkProvider(ChainName.Regtest);
        var bitcoin = nbx.GetFromCryptoCode("BTC");
        var network = new BTCPayNetwork { CryptoCode = "BTC", NBXplorerNetwork = bitcoin };
        var networks = new BTCPayNetworkProvider([network], nbx, new Logs());
        var serializer = JsonSerializer.Create(bitcoin.Serializer.Settings);
        var method = PaymentTypes.CHAIN.GetPaymentMethodId("BTC");
        var handler = Substitute.For<IPaymentMethodHandler>();
        handler.PaymentMethodId.Returns(method);
        ((IHandler<PaymentMethodId>)handler).Id.Returns(method);
        handler.Serializer.Returns(serializer);
        Exception? profileError = null;
        handler.ParsePaymentMethodConfig(Arg.Any<JToken>()).Returns(call =>
            profileError is not null ? throw profileError : ((JToken)call[0]).ToObject<DerivationSchemeSettings>(serializer)!);
        handler.ParsePaymentPromptDetails(Arg.Any<JToken>()).Returns(new BitcoinPaymentPromptDetails { RecommendedFeeRate = new FeeRate(1m) });
        var account = bitcoin.DerivationStrategyFactory.CreateDirectDerivationStrategy(new ExtKey().Neuter(), new DerivationStrategyOptions());
        var wallet = new DerivationSchemeSettings { IsHotWallet = hot, Source = source, AccountDerivation = accountAvailable ? account : null! };
        var store = new BTCPayServer.Data.StoreData { Id = "store-1" };
        store.SetPaymentMethodConfig(handler, wallet);
        var blob = new StoreBlob { AdditionalData = new JObject { ["payjoin.settings"] = JToken.FromObject(new PayjoinStoreSettings { PayjoinV2Enabled = enabled }) } };
        var invoice = new InvoiceEntity { Id = InvoiceId, StoreId = store.Id, Status = InvoiceStatus.New, Currency = "BTC", Price = DueBtc };
        var prompt = new PaymentPrompt { Currency = "BTC", Destination = Destination, Divisibility = 8, Details = new JObject() };
        invoice.SetPaymentPrompt(method, prompt);
        invoice.UpdateTotals();
        var urls = Substitute.For<IUrlHelper>();
        urls.Action(Arg.Any<UrlActionContext>()).Returns(PaymentUrlEndpoint);
        var logger = Substitute.For<ILogger<PayjoinBitcoinCheckoutModelExtension>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var handlers = new PaymentMethodHandlerDictionary([handler]);
        var extension = new PayjoinBitcoinCheckoutModelExtension(networks, handlers, reader, logger);
        CheckoutModel Render()
        {
            var model = CreateModel();
            model.Status = "New";
            model.Address = Destination;
            extension.ModifyCheckoutModel(new CheckoutModelContext(model, store, blob, invoice, urls, prompt, handler));
            var action = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
            extension.OnResultExecuting(new ResultExecutingContext(action, [], new JsonResult(model), new object()));
            return model;
        }

        var plainQr = Render().InvoiceBitcoinUrlQR;
        var originalConfig = store.DerivationStrategies;
        if (failure == "json")
        {
            store.DerivationStrategies = "{";
        }
        profileError = failure switch
        {
            "format" => new FormatException("Invalid wallet derivation"),
            "cancel" => new OperationCanceledException(),
            _ => null
        };
        if (failure == "cancel")
        {
            Assert.Same(profileError, Assert.Throws<OperationCanceledException>(() => Render()));
        }
        else
        {
            var rendered = Render();
            Assert.Equal(bootstrap, rendered.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PayjoinV2EnabledKey].ToObject<bool>());
            Assert.Equal(publish, HasPayjoinUrl(rendered));
            Assert.Equal(bootstrap && !publish, rendered.AdditionalData[PayjoinBitcoinCheckoutModelExtension.CanBootstrapKey].ToObject<bool>());
            Assert.Equal(PlainUrl, rendered.InvoiceBitcoinUrl);
            Assert.Equal(plainQr, rendered.InvoiceBitcoinUrlQR);
            Assert.Equal(PlainUrl, rendered.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PlainBitcoinUrlKey].ToObject<string>());
            Assert.Equal(PlainUrl, rendered.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PlainBitcoinUrlQrKey].ToObject<string>());
            Assert.Equal(PaymentUrlEndpoint, rendered.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PayjoinPaymentUrlEndpointKey].ToObject<string>());
            if (failure is "json" or "format")
            {
                var log = Assert.Single(logger.ReceivedCalls(), call => call.GetMethodInfo().Name == "Log");
                Assert.IsAssignableFrom<Exception>(log.GetArguments()[3]);
                Assert.Equal(LogLevel.Warning, log.GetArguments()[0]);
                Assert.Equal(new EventId(1, nameof(extension.ModifyCheckoutModel)), log.GetArguments()[1]);
            }
        }
        Assert.True(sessions.TryGetSession(InvoiceId, out var retained));
        Assert.Equal(before.PayjoinUri, retained!.PayjoinUri);
        Assert.Equal(events, retained.GetEvents());
        Assert.Equal(enabled, PayjoinStoreSettingsRepository.ReadSettings(blob)!.PayjoinV2Enabled);

        profileError = null;
        store.DerivationStrategies = originalConfig;
        wallet.IsHotWallet = true;
        wallet.Source = source is "NBXplorerGenerated" or "ImportedSeed" ? source : "NBXplorerGenerated";
        wallet.AccountDerivation = account;
        store.SetPaymentMethodConfig(handler, wallet);
        var recovered = Render();
        Assert.Equal(enabled, HasPayjoinUrl(recovered));
        if (enabled)
        {
            Assert.Equal(PayjoinUri,
                recovered.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PayjoinBitcoinUrlKey].ToObject<string>());
        }
        Assert.True(sessions.TryGetSession(InvoiceId, out var restored));
        Assert.Equal(before.PayjoinUri, restored!.PayjoinUri);
        Assert.Equal(events, restored.GetEvents());
        Assert.Equal(PlainUrl, recovered.InvoiceBitcoinUrl);
        Assert.Equal(plainQr, recovered.InvoiceBitcoinUrlQR);
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
