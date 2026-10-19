using BTCPayServer;
using BTCPayServer.Data;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Models.InvoicingModels;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Rates;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class PluginRegistrationTests
{
    [Fact]
    public void PluginRegistersBitcoinCheckoutExtensions()
    {
        var services = new ServiceCollection();

        new Plugin().Execute(services);

        var uiExtensions = services
            .Where(descriptor => descriptor.ServiceType == typeof(IUIExtension))
            .Select(descriptor => Assert.IsAssignableFrom<IUIExtension>(descriptor.ImplementationInstance))
            .ToArray();

        Assert.DoesNotContain(uiExtensions, extension =>
            extension.Location == "checkout-bitcoin-post-content" &&
            extension.Partial == "PayJoinBitcoinCheckoutPostContent");
        Assert.Contains(uiExtensions, extension =>
            extension.Location == "checkout-end" &&
            extension.Partial == "PayJoinBitcoinCheckoutEnd");
        Assert.DoesNotContain(uiExtensions, extension =>
            extension.Location == "store-invoices-payments" &&
            extension.Partial == "PayjoinInvoiceOutcome");
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(ISwaggerProvider) &&
            descriptor.ImplementationType == typeof(PayjoinSwaggerProvider));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IPayjoinReceiverInputSelector) &&
            descriptor.ImplementationType == typeof(PayjoinReceiverInputSelector));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IPayjoinReceiverWalletAdapter) &&
            descriptor.ImplementationType == typeof(PayjoinReceiverWalletAdapter));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IPayjoinReceiverInputProposalOperations) &&
            descriptor.ImplementationType == typeof(PayjoinReceiverInputProposalOperations));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(PayjoinBridgeAttentionService) &&
            descriptor.ImplementationFactory is not null);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IPayjoinAttentionRecordSeeder) &&
            descriptor.ImplementationType == typeof(PayjoinAttentionRecordSeeder) &&
            descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IPayjoinInvoiceEventPublisher) &&
            descriptor.ImplementationType == typeof(PayjoinInvoiceEventPublisher) &&
            descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IPayjoinInvoiceEventWriter) &&
            descriptor.ImplementationType == typeof(PayjoinInvoiceEventWriter));
        var hostedServices = services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).ToArray();
        var migrationIndex = Array.FindIndex(hostedServices, descriptor => descriptor.ImplementationType == typeof(PluginMigrationRunner));
        var writerIndex = Array.FindIndex(hostedServices, descriptor => descriptor.ImplementationType == typeof(PayjoinInvoiceEventHostedService));
        var pollerIndex = Array.FindIndex(hostedServices, descriptor => descriptor.ImplementationFactory is not null);
        Assert.True(migrationIndex >= 0 && writerIndex > migrationIndex && pollerIndex > writerIndex);
    }

    [Fact]
    public void PluginPreservesCheckoutExtensionsAndSharesItsFilterWithinEachRequest()
    {
        using var fixture = new SessionStoreFixture();
        var services = CreateBitcoinPluginServices();
        var unrelatedPaymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId("LTC");
        var unrelatedExtension = new TestCheckoutModelExtension(unrelatedPaymentMethodId);
        services.AddSingleton<ICheckoutModelExtension>(unrelatedExtension);

        new Plugin().Execute(services);
        services.AddSingleton(new PayjoinSessionUriReader(fixture.CreateStore()));

        using var provider = services.BuildServiceProvider();
        var paymentExtensions = provider.GetRequiredService<Dictionary<PaymentMethodId, ICheckoutModelExtension>>();

        Assert.IsType<BitcoinCheckoutModelExtension>(
            paymentExtensions[PaymentTypes.CHAIN.GetPaymentMethodId(PayjoinConstants.BitcoinCode)]);
        Assert.Same(unrelatedExtension, paymentExtensions[unrelatedPaymentMethodId]);
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var global = Assert.Single(first.ServiceProvider.GetServices<IGlobalCheckoutModelExtension>());
        Assert.Same(global, first.ServiceProvider.GetRequiredService<PayjoinBitcoinCheckoutModelExtension>());
        Assert.NotSame(global, second.ServiceProvider.GetRequiredService<PayjoinBitcoinCheckoutModelExtension>());
        Assert.Contains(provider.GetRequiredService<IOptions<MvcOptions>>().Value.Filters,
            filter => filter is ServiceFilterAttribute service && service.ServiceType == global.GetType());
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void ReplacedCheckoutIsComposedAfterEveryExtension(bool payjoinLast, bool ready, bool json)
    {
        using var fixture = new SessionStoreFixture();
        using var key = new Key();
        var address = key.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest).ToString();
        var payjoinUri = $"bitcoin:{address}?amount=0.1&pjos=0&pj=https%3A%2F%2Fexample.com%2Fpj";
        var store = fixture.CreateStore();
        if (ready)
        {
            store.GetOrCreateSession("invoice", address, "store", DateTimeOffset.UtcNow.AddMinutes(15), ["event"]);
            store.StorePayjoinUri("invoice", address, payjoinUri);
        }
        var services = CreateBitcoinPluginServices();
        var replacement = new ReplacementCheckoutExtension(address);
        if (payjoinLast)
            services.AddSingleton<IGlobalCheckoutModelExtension>(replacement);
        new Plugin().Execute(services);
        services.AddSingleton(new PayjoinSessionUriReader(store));
        if (!payjoinLast)
            services.AddSingleton<IGlobalCheckoutModelExtension>(replacement);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var invoice = new InvoiceEntity { Id = "invoice", Currency = "BTC", Price = 0.1m };
        var bitcoinId = PaymentMethodId.Parse("BTC-CHAIN");
        invoice.SetPaymentPrompt(bitcoinId, new PaymentPrompt { Currency = "BTC", Destination = address, Divisibility = 8 });
        invoice.UpdateTotals();
        var model = new CheckoutModel { InvoiceId = "invoice", Status = "New", PaymentMethodId = "BTC-CHAIN" };
        var blob = new StoreBlob { AdditionalData = JObject.Parse("{\"payjoin.settings\":{\"PayjoinV2Enabled\":true}}") };
        var url = Substitute.For<IUrlHelper>();
        url.Action(Arg.Any<Microsoft.AspNetCore.Mvc.Routing.UrlActionContext>()).Returns("/plugins/payjoin/invoices/invoice/payment-url");
        var handlers = provider.GetRequiredService<PaymentMethodHandlerDictionary>();
        var network = provider.GetRequiredService<BTCPayNetworkProvider>().GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode)!;
        var walletStore = new StoreData();
        walletStore.SetPaymentMethodConfig(handlers[bitcoinId], new DerivationSchemeSettings
        {
            IsHotWallet = true,
            Source = "NBXplorerGenerated",
            AccountDerivation = network.NBXplorerNetwork.DerivationStrategyFactory.CreateDirectDerivationStrategy(
                new ExtKey().Neuter(), new DerivationStrategyOptions())
        });
        var context = new CheckoutModelContext(model, walletStore, blob, invoice, url, invoice.GetPaymentPrompt(bitcoinId)!, null!);
        foreach (var extension in scope.ServiceProvider.GetServices<IGlobalCheckoutModelExtension>())
            extension.ModifyCheckoutModel(context);
        Assert.False(model.AdditionalData.ContainsKey(PayjoinBitcoinCheckoutModelExtension.PlainBitcoinUrlKey));
        IActionResult result = json ? new JsonResult(model) : new ViewResult
        {
            ViewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary()) { Model = model }
        };
        var action = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
        scope.ServiceProvider.GetRequiredService<PayjoinBitcoinCheckoutModelExtension>()
            .OnResultExecuting(new ResultExecutingContext(action, [], result, new object()));
        var plain = $"bitcoin:{address}?amount=0.1&pj=http://localhost/BTC/pj&ark=Mixed%2fCase&lightning=lnbcrt123&label=Store%20Name&message=Order%2f123&pop=https%3a%2f%2fexample.com%2fproof&custom=Keep%2fCase";
        Assert.Equal("FIXTURE", model.PaymentMethodId);
        Assert.Equal("FixtureCheckout", model.CheckoutBodyComponentName);
        Assert.Equal(plain, model.InvoiceBitcoinUrl);
        Assert.Equal(plain, model.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PlainBitcoinUrlKey].Value<string>());
        Assert.Equal(plain, model.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PlainBitcoinUrlQrKey].Value<string>());
        Assert.Equal(!ready, model.AdditionalData[PayjoinBitcoinCheckoutModelExtension.CanBootstrapKey].Value<bool>());
        if (ready)
        {
            Assert.Equal(payjoinUri, model.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PayjoinBitcoinUrlKey].Value<string>());
            Assert.Equal(payjoinUri, model.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PayjoinBitcoinUrlQrKey].Value<string>());
        }
        else
            Assert.Empty(store.GetSessions());
    }

    private static IServiceCollection CreateBitcoinPluginServices()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(PayjoinConstants.BitcoinCode);
        var nbxplorerNetworkProvider = new NBXplorerNetworkProvider(ChainName.Regtest);
        var network = CreateBitcoinNetwork(nbxplorerNetworkProvider);

        services.AddSingleton(_ => new BTCPayNetworkProvider([network], nbxplorerNetworkProvider, new Logs()));
        var serializer = JsonSerializer.Create(network.NBXplorerNetwork.Serializer.Settings);
        var handler = Substitute.For<IPaymentMethodHandler>();
        handler.PaymentMethodId.Returns(paymentMethodId);
        ((IHandler<PaymentMethodId>)handler).Id.Returns(paymentMethodId);
        handler.Serializer.Returns(serializer);
        handler.ParsePaymentMethodConfig(Arg.Any<JToken>()).Returns(call => ((JToken)call[0]).ToObject<DerivationSchemeSettings>(serializer)!);
        services.AddSingleton(new PaymentMethodHandlerDictionary([handler]));
        services.AddSingleton<IPaymentLinkExtension>(new TestPaymentLinkExtension(paymentMethodId));
        services.AddSingleton<ICheckoutModelExtension>(provider => new BitcoinCheckoutModelExtension(
            paymentMethodId,
            network,
            provider.GetServices<IPaymentLinkExtension>(),
            provider.GetRequiredService<DisplayFormatter>()));
        services.AddSingleton(_ => new CurrencyNameTable(
            [
                new InMemoryCurrencyDataProvider(
                [
                    new CurrencyData
                    {
                        Code = "USD",
                        Name = "US Dollar",
                        Divisibility = 2,
                        Symbol = "$",
                        Crypto = false
                    },
                    new CurrencyData
                    {
                        Code = "BTC",
                        Name = "Bitcoin",
                        Divisibility = 8,
                        Symbol = "BTC",
                        Crypto = true
                    }
                ])
            ],
            NullLogger<CurrencyNameTable>.Instance));
        services.AddSingleton<DisplayFormatter>();
        services.AddSingleton(provider => provider.GetServices<ICheckoutModelExtension>().ToDictionary(extension => extension.PaymentMethodId));

        return services;
    }

    private sealed class ReplacementCheckoutExtension(string address) : IGlobalCheckoutModelExtension
    {
        public void ModifyCheckoutModel(CheckoutModelContext context)
        {
            context.Model.PaymentMethodId = "FIXTURE";
            context.Model.CheckoutBodyComponentName = "FixtureCheckout";
            context.Model.InvoiceBitcoinUrl = $"bitcoin:{address}?amount=0.1&pj=http://localhost/BTC/pj&ark=Mixed%2fCase&lightning=lnbcrt123&label=Store%20Name&message=Order%2f123&pop=https%3a%2f%2fexample.com%2fproof&custom=Keep%2fCase";
            context.Model.InvoiceBitcoinUrlQR = context.Model.InvoiceBitcoinUrl.ToUpperInvariant();
        }
    }

    private static BTCPayNetwork CreateBitcoinNetwork(NBXplorerNetworkProvider nbxplorerNetworkProvider)
    {
        return new BTCPayNetwork
        {
            CryptoCode = PayjoinConstants.BitcoinCode,
            DisplayName = "Bitcoin",
            NBXplorerNetwork = nbxplorerNetworkProvider.GetFromCryptoCode(PayjoinConstants.BitcoinCode),
            CryptoImagePath = "imlegacy/bitcoin.svg",
            LightningImagePath = "imlegacy/bitcoin-lightning.svg",
            DefaultSettings = new BTCPayDefaultSettings(),
            CoinType = new KeyPath("1'"),
            SupportRBF = true,
            SupportPayJoin = true,
            VaultSupported = true
        }.SetDefaultElectrumMapping(ChainName.Regtest);
    }

    private sealed class TestCheckoutModelExtension(PaymentMethodId paymentMethodId) : ICheckoutModelExtension
    {
        public PaymentMethodId PaymentMethodId { get; } = paymentMethodId;
        public string Image => string.Empty;
        public string Badge => string.Empty;

        public void ModifyCheckoutModel(CheckoutModelContext context)
        {
        }
    }

    private sealed class TestPaymentLinkExtension(PaymentMethodId paymentMethodId) : IPaymentLinkExtension
    {
        public PaymentMethodId PaymentMethodId { get; } = paymentMethodId;

        public string? GetPaymentLink(PaymentPrompt prompt, IUrlHelper? urlHelper)
        {
            return null;
        }
    }
}
