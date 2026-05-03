using BTCPayServer;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Rates;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NBXplorer;
using System.Collections.Generic;
using System.Linq;
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

        Assert.Contains(uiExtensions, extension =>
            extension.Location == "checkout-bitcoin-post-content" &&
            extension.Partial == "PayJoinBitcoinCheckoutPostContent");
        Assert.Contains(uiExtensions, extension =>
            extension.Location == "checkout-end" &&
            extension.Partial == "PayJoinBitcoinCheckoutEnd");
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(ISwaggerProvider) &&
            descriptor.ImplementationType == typeof(PayjoinSwaggerProvider));
    }

    [Fact]
    public void PluginReplacesBitcoinCheckoutDictionaryEntryAndPreservesUnrelatedEntries()
    {
        var services = CreateBitcoinPluginServices();
        var unrelatedPaymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId("LTC");
        var unrelatedExtension = new TestCheckoutModelExtension(unrelatedPaymentMethodId);
        services.AddSingleton<ICheckoutModelExtension>(unrelatedExtension);

        new Plugin().Execute(services);

        using var provider = services.BuildServiceProvider();
        var paymentExtensions = provider.GetRequiredService<Dictionary<PaymentMethodId, ICheckoutModelExtension>>();

        Assert.IsType<PayjoinBitcoinCheckoutModelExtension>(
            paymentExtensions[PaymentTypes.CHAIN.GetPaymentMethodId("BTC")]);
        Assert.Same(unrelatedExtension, paymentExtensions[unrelatedPaymentMethodId]);
    }

    private static IServiceCollection CreateBitcoinPluginServices()
    {
        var services = new ServiceCollection();
        var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId("BTC");
        var nbxplorerNetworkProvider = new NBXplorerNetworkProvider(ChainName.Regtest);
        var network = CreateBitcoinNetwork(nbxplorerNetworkProvider);

        services.AddSingleton(_ => new BTCPayNetworkProvider([network], nbxplorerNetworkProvider, new Logs()));
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

        return services;
    }

    private static BTCPayNetwork CreateBitcoinNetwork(NBXplorerNetworkProvider nbxplorerNetworkProvider)
    {
        return new BTCPayNetwork
        {
            CryptoCode = "BTC",
            DisplayName = "Bitcoin",
            NBXplorerNetwork = nbxplorerNetworkProvider.GetFromCryptoCode("BTC"),
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
