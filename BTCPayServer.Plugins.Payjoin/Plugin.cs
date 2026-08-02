using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.Payjoin;

public class Plugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new IBTCPayServerPlugin.PluginDependency { Identifier = nameof(BTCPayServer), Condition = ">=2.4.0" }
    };

    public override void Execute(IServiceCollection applicationBuilder)
    {
        applicationBuilder.AddHttpClient();
        applicationBuilder.AddUIExtension("header-nav", "PayjoinHeaderNav");
        applicationBuilder.AddUIExtension("store-nav", "PayjoinStoreNavExtension");
        applicationBuilder.AddUIExtension("checkout-bitcoin-post-content", "PayJoinBitcoinCheckoutPostContent");
        applicationBuilder.AddUIExtension("checkout-end", "PayJoinBitcoinCheckoutEnd");
        applicationBuilder.AddSingleton<PayjoinAvailabilityService>();
        applicationBuilder.AddSingleton<PayjoinSessionUriReader>();
        applicationBuilder.AddSingleton<PayjoinBitcoinCheckoutModelExtension>();
        applicationBuilder.AddSingleton<IPayjoinUniqueConstraintViolationDetector, PostgresPayjoinUniqueConstraintViolationDetector>();
        applicationBuilder.AddSingleton(provider => new PayjoinReceiverSessionStore(
            provider.GetRequiredService<PayjoinPluginDbContextFactory>(),
            provider.GetRequiredService<IPayjoinUniqueConstraintViolationDetector>(),
            provider.GetService<ILogger<PayjoinReceiverSessionStore>>()));
        applicationBuilder.AddSingleton(provider => new PayjoinSeenInputStore(
            provider.GetRequiredService<PayjoinPluginDbContextFactory>()));
        applicationBuilder.AddSingleton<IPayjoinWalletOwnershipService, PayjoinWalletOwnershipService>();
        applicationBuilder.AddSingleton<IPayjoinFeeRateProvider, PayjoinFeeRateProvider>();
        applicationBuilder.AddSingleton<IPayjoinReceiverSessionGuard, PayjoinReceiverSessionGuard>();
        applicationBuilder.AddSingleton<IPayjoinReceiverRelayClient, PayjoinReceiverRelayClient>();
        applicationBuilder.AddSingleton<IPayjoinReceiverRelayRequestSender, PayjoinReceiverRelayRequestSender>();
        applicationBuilder.AddSingleton<IPayjoinReceiverStateProcessor, PayjoinReceiverStateProcessor>();
        applicationBuilder.AddSingleton<IPayjoinReceiverOutputBuilder, PayjoinReceiverOutputBuilder>();
        applicationBuilder.AddSingleton<IPayjoinReceiverWalletAdapter, PayjoinReceiverWalletAdapter>();
        applicationBuilder.AddSingleton<IPayjoinReceiverInputProposalOperations, PayjoinReceiverInputProposalOperations>();
        applicationBuilder.AddSingleton<IPayjoinReceiverInputSelector, PayjoinReceiverInputSelector>();
        applicationBuilder.AddSingleton<IPayjoinAccountingBridgeService>(provider => new PayjoinAccountingBridgeService(
            provider.GetRequiredService<PayjoinPluginDbContextFactory>(),
            provider.GetRequiredService<IPayjoinUniqueConstraintViolationDetector>(),
            provider.GetRequiredService<PayjoinSessionBuildLock>()));
        applicationBuilder.AddSingleton<IPayjoinAttentionRecordSeeder>(provider =>
            (IPayjoinAttentionRecordSeeder)provider.GetRequiredService<IPayjoinAccountingBridgeService>());
        applicationBuilder.AddSingleton<IPayjoinStalePaidOverCorrectionService, PayjoinStalePaidOverCorrectionService>();
        applicationBuilder.AddSingleton<IPayjoinPlatformPaymentRecorder, PayjoinPlatformPaymentRecorder>();
        applicationBuilder.AddSingleton<IPayjoinWalletTransactionReader, PayjoinWalletTransactionReader>();
        applicationBuilder.AddSingleton<IPayjoinTransactionLabeler, PayjoinTransactionLabeler>();
        applicationBuilder.AddSingleton(provider => new PayjoinBridgeAttentionService(
            provider.GetRequiredService<IPayjoinAccountingBridgeService>()));
        applicationBuilder.AddSingleton<IPayjoinAccountingPaymentService, PayjoinAccountingPaymentService>();
        applicationBuilder.AddSingleton<IPayjoinReceiverProposalSigner, PayjoinReceiverProposalSigner>();
        applicationBuilder.AddSingleton<IPayjoinReceiverProposalFinalizer, PayjoinReceiverProposalFinalizer>();
        applicationBuilder.AddSingleton<IPayjoinReceiverSessionProcessor, PayjoinReceiverSessionProcessor>();
        applicationBuilder.AddSingleton<PayjoinOhttpKeysProvider>();
        applicationBuilder.AddSingleton<PayjoinMailroomManager>();
        applicationBuilder.AddSingleton<PayjoinSessionBuildLock>();
        applicationBuilder.AddSingleton(provider => new PayjoinUriSessionService(
            provider.GetRequiredService<BTCPayNetworkProvider>(),
            provider.GetRequiredService<PayjoinReceiverSessionStore>(),
            provider.GetRequiredService<PayjoinMailroomManager>(),
            provider.GetRequiredService<PayjoinAvailabilityService>(),
            provider.GetRequiredService<PayjoinSessionBuildLock>(),
            provider.GetRequiredService<IPayjoinAccountingBridgeService>(),
            provider.GetRequiredService<IPayjoinFeeRateProvider>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PayjoinUriSessionService>>()));
        applicationBuilder.AddSingleton<ISwaggerProvider, PayjoinSwaggerProvider>();
        applicationBuilder.AddSingleton<IPayjoinInvoiceLookup, PayjoinInvoiceLookup>();
        applicationBuilder.AddSingleton<PayjoinInvoicePaymentUrlService>();
        applicationBuilder.AddSingleton<IPayjoinInvoicePaymentUrlService>(provider => provider.GetRequiredService<PayjoinInvoicePaymentUrlService>());
        applicationBuilder.AddSingleton(provider => new PayjoinSenderSessionStore(
            provider.GetRequiredService<PayjoinPluginDbContextFactory>(),
            provider.GetRequiredService<IPayjoinUniqueConstraintViolationDetector>()));
        applicationBuilder.AddSingleton(provider => new PayjoinSenderService(
            provider.GetRequiredService<BTCPayNetworkProvider>(),
            provider.GetRequiredService<BTCPayServer.Services.Stores.StoreRepository>(),
            provider.GetRequiredService<BTCPayServer.Services.Invoices.PaymentMethodHandlerDictionary>(),
            provider.GetRequiredService<ExplorerClientProvider>(),
            provider.GetRequiredService<BTCPayServer.Services.IFeeProviderFactory>(),
            provider.GetRequiredService<PayjoinSenderSessionStore>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PayjoinSenderService>>()));
        applicationBuilder.AddSingleton<IPayjoinSenderSessionProcessor>(provider => new PayjoinSenderSessionProcessor(
            provider.GetRequiredService<PayjoinSenderSessionStore>(),
            provider.GetRequiredService<IPayjoinStoreSettingsRepository>(),
            provider.GetRequiredService<IPayjoinReceiverRelayClient>(),
            provider.GetRequiredService<BTCPayNetworkProvider>(),
            provider.GetRequiredService<BTCPayServer.Services.Stores.StoreRepository>(),
            provider.GetRequiredService<BTCPayServer.Services.Invoices.PaymentMethodHandlerDictionary>(),
            provider.GetRequiredService<ExplorerClientProvider>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PayjoinSenderSessionProcessor>>()));
        applicationBuilder.AddHostedService(provider => new PayjoinSenderPoller(
            provider.GetRequiredService<IPayjoinSenderSessionProcessor>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PayjoinSenderPoller>>()));
        applicationBuilder.AddHostedService<PluginMigrationRunner>();
        applicationBuilder.AddHostedService(provider => new PayjoinReceiverPoller(
            provider.GetRequiredService<PayjoinReceiverSessionStore>(),
            provider.GetRequiredService<IPayjoinReceiverSessionProcessor>(),
            provider.GetRequiredService<IPayjoinAccountingBridgeService>(),
            provider.GetRequiredService<IPayjoinAccountingPaymentService>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PayjoinReceiverPoller>>()));
        applicationBuilder.AddHostedService<PayjoinInvoiceSessionLifecycleService>();
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
