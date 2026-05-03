using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.Payjoin;

public class Plugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new IBTCPayServerPlugin.PluginDependency { Identifier = nameof(BTCPayServer), Condition = ">=2.0.0" }
    };

    public override void Execute(IServiceCollection applicationBuilder)
    {
        applicationBuilder.AddHttpClient();
        applicationBuilder.AddUIExtension("header-nav", "PayjoinHeaderNav");
        applicationBuilder.AddUIExtension("store-nav", "PayjoinStoreNavExtension");
        applicationBuilder.AddUIExtension("checkout-bitcoin-post-content", "PayJoinBitcoinCheckoutPostContent");
        applicationBuilder.AddUIExtension("checkout-end", "PayJoinBitcoinCheckoutEnd");
        applicationBuilder.AddSingleton<PayjoinAvailabilityService>();
        applicationBuilder.AddSingleton<PayjoinBitcoinCheckoutModelExtension>();
        applicationBuilder.AddSingleton<PayjoinReceiverSessionStore>();
        applicationBuilder.AddSingleton<PayjoinOhttpKeysProvider>();
        applicationBuilder.AddSingleton<PayjoinSessionBuildLock>();
        applicationBuilder.AddSingleton<PayjoinUriSessionService>();
        applicationBuilder.AddSingleton<ISwaggerProvider, PayjoinSwaggerProvider>();
        applicationBuilder.AddSingleton<IPayjoinInvoiceLookup, PayjoinInvoiceLookup>();
        applicationBuilder.AddSingleton<PayjoinInvoicePaymentUrlService>();
        applicationBuilder.AddSingleton<IPayjoinInvoicePaymentUrlService>(provider => provider.GetRequiredService<PayjoinInvoicePaymentUrlService>());
        applicationBuilder.AddHostedService<PluginMigrationRunner>();
        applicationBuilder.AddHostedService<PayjoinReceiverPoller>();
        applicationBuilder.AddHostedService<PayjoinInvoiceSessionLifecycleService>();
        applicationBuilder.AddSingleton<PayjoinPluginService>();
        applicationBuilder.AddSingleton<IPayjoinStoreSettingsRepository, PayjoinStoreSettingsRepository>();
        applicationBuilder.AddSingleton<PayjoinPluginDbContextFactory>();
        applicationBuilder.AddSingleton<IRunTestPaymentService, RunTestPaymentService>();
        applicationBuilder.AddDbContext<PayjoinPluginDbContext>((provider, o) =>
        {
            var factory = provider.GetRequiredService<PayjoinPluginDbContextFactory>();
            factory.ConfigureBuilder(o);
        });
        // BTCPay resolves checkout extensions through this dictionary, so replace the BTC entry here
        // instead of registering a second ICheckoutModelExtension for the same PaymentMethodId.
        applicationBuilder.Replace(ServiceDescriptor.Singleton<Dictionary<PaymentMethodId, ICheckoutModelExtension>>(provider =>
        {
            var payjoinExtension = provider.GetRequiredService<PayjoinBitcoinCheckoutModelExtension>();
            var extensions = provider.GetRequiredService<IEnumerable<ICheckoutModelExtension>>();
            var paymentExtensions = new Dictionary<PaymentMethodId, ICheckoutModelExtension>();
            foreach (var extension in extensions)
            {
                paymentExtensions[extension.PaymentMethodId] =
                    extension.PaymentMethodId == payjoinExtension.PaymentMethodId
                        ? payjoinExtension
                        : extension;
            }

            paymentExtensions[payjoinExtension.PaymentMethodId] = payjoinExtension;
            return paymentExtensions;
        }));
    }
}
