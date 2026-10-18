using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Logging;
using BTCPayServer.HostedServices;
using NBXplorer.Models;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using NBXplorer.DerivationStrategy;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBXplorer;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using NSubstitute;
using System.Security.Claims;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class UIPayjoinOverviewControllerTests
{
    private const string StoreId = "store-1";
    private const string ExpectedTransactionId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task IndexListsTheBridgesRequiringAttentionForTheCurrentStore()
    {
        using var context = new TestContext();
        var now = DateTimeOffset.UtcNow;
        await context.CreateBridgeAsync("invoice-pending", expiresAt: now.AddHours(1));
        await context.CreateBridgeAsync("invoice-failed", expiresAt: now.AddHours(1));
        await context.BridgeService.MarkFailedAsync("invoice-failed", "reconciliation data problem", CancellationToken.None);
        var pastGrace = now - PayjoinAccountingBridgeService.ArmedBridgeGracePeriod - TimeSpan.FromMinutes(1);
        await context.CreateBridgeAsync("invoice-armed-expired", expiresAt: pastGrace, expectedFinalTransactionId: ExpectedTransactionId);
        await context.CreateBridgeAsync("invoice-unarmed-expired", expiresAt: now.AddMinutes(-5));
        await context.CreateBridgeAsync("invoice-other-store", expiresAt: now.AddHours(1), storeId: "other-store");
        await context.BridgeService.MarkFailedAsync("invoice-other-store", "other store failure", CancellationToken.None);
        await context.BridgeService.ExpirePendingAsync(now, CancellationToken.None);
        using var controller = context.CreateController();

        var result = await controller.Index();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<PayjoinOverviewViewModel>(view.Model);
        Assert.True(model.CanRetryBridges);
        Assert.Equal(2, model.AttentionBridges.Count);
        Assert.Equal(2, model.AttentionBridgesTotalCount);
        var failed = Assert.Single(model.AttentionBridges, x => x.InvoiceId == "invoice-failed");
        Assert.True(failed.IsFailed);
        Assert.Equal("reconciliation data problem", failed.FailureMessage);
        var expired = Assert.Single(model.AttentionBridges, x => x.InvoiceId == "invoice-armed-expired");
        Assert.False(expired.IsFailed);
        Assert.Equal(ExpectedTransactionId, expired.ExpectedFinalTransactionId);
    }

    [Fact]
    public async Task IndexHidesTheRetryActionWithoutTheModifyStoreSettingsPermission()
    {
        using var context = new TestContext(canModifyStoreSettings: false);
        using var controller = context.CreateController();

        var result = await controller.Index();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<PayjoinOverviewViewModel>(view.Model);
        Assert.False(model.CanRetryBridges);
    }

    [Fact]
    public async Task IndexDoesNotQueryReceiverInputsWhenAsyncPayjoinIsDisabled()
    {
        using var context = new TestContext(payjoinV2Enabled: false, registerBitcoinNetwork: true);
        using var controller = context.CreateController();

        var result = await controller.Index();

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<PayjoinOverviewViewModel>(view.Model);
        var store = Assert.IsType<CurrentStorePayjoinStatusViewModel>(model.CurrentStore);
        Assert.Equal("secondary", store.Status.Severity);
        Assert.Equal("Disabled", store.Status.Title);
        Assert.Null(store.HasConfirmedReceiverInputs);
    }

    [Theory]
    [InlineData(false, "NBXplorerGenerated", true, "danger", "Needs configuration")]
    [InlineData(true, "ImportedSeed", true, "danger", "Needs configuration")]
    [InlineData(true, "NBXplorerGenerated", false, "warning", "Unavailable")]
    public async Task IndexExplainsProfileBeforeAnyUnavailableCoinLookup(
        bool hot, string source, bool accountAvailable, string severity, string title)
    {
        using var context = new TestContext(registerBitcoinNetwork: true);
        var bitcoin = new NBXplorerNetworkProvider(ChainName.Regtest).GetFromCryptoCode("BTC");
        var serializer = JsonSerializer.Create(bitcoin.Serializer.Settings);
        var handler = Substitute.For<IPaymentMethodHandler>();
        var paymentMethod = PaymentTypes.CHAIN.GetPaymentMethodId("BTC");
        handler.PaymentMethodId.Returns(paymentMethod);
        ((IHandler<PaymentMethodId>)handler).Id.Returns(paymentMethod);
        handler.Serializer.Returns(serializer);
        handler.ParsePaymentMethodConfig(Arg.Any<JToken>()).Returns(call => ((JToken)call[0]).ToObject<DerivationSchemeSettings>(serializer)!);
        using var controller = context.CreateController(new PaymentMethodHandlerDictionary([handler]));
        var store = controller.HttpContext.GetNavStoreData()!;
        store.SetPaymentMethodConfig(handler, new DerivationSchemeSettings
        {
            IsHotWallet = hot, Source = source,
            AccountDerivation = accountAvailable ? bitcoin.DerivationStrategyFactory.CreateDirectDerivationStrategy(
                new ExtKey().Neuter(), new DerivationStrategyOptions()) : null!
        });

        var view = Assert.IsType<ViewResult>(await controller.Index());
        var model = Assert.IsType<PayjoinOverviewViewModel>(view.Model).CurrentStore!;
        Assert.Null(model.HasConfirmedReceiverInputs);
        Assert.Equal(severity, model.Status.Severity);
        Assert.Equal(title, model.Status.Title);
        Assert.Contains("not checked", model.Status.Message, StringComparison.Ordinal);
        Assert.Contains("standard Bitcoin payment", model.Status.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("no confirmed receiver inputs", model.Status.Message, StringComparison.Ordinal);
        if (accountAvailable)
        {
            Assert.Contains("BTCPay-generated hot wallet", model.Status.Message, StringComparison.Ordinal);
            Assert.Equal(PayjoinCheckoutMode.StandardBitcoin, model.DefaultCheckoutMode);
        }
        else
        {
            Assert.Contains("could not be confirmed", model.Status.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("json", true, true, false)]
    [InlineData("format", true, true, false)]
    [InlineData("json", false, true, false)]
    [InlineData("format", false, true, false)]
    [InlineData("json", true, false, false)]
    [InlineData("format", true, false, false)]
    [InlineData("json", true, true, true)]
    [InlineData("format", true, true, true)]
    [InlineData("cancel", true, true, false)]
    [InlineData("cancel", false, true, false)]
    [InlineData("cancel", true, false, false)]
    [InlineData("cancel", true, true, true)]
    [InlineData(null, true, true, false)]
    [InlineData(null, false, true, false)]
    public async Task IndexHandlesProfileReadErrorsIncludingTheV1Fallback(
        string? failure, bool enabled, bool settingsAvailable, bool failSecondRead)
    {
        using var context = new TestContext(payjoinV2Enabled: enabled, registerBitcoinNetwork: true, settingsAvailable: settingsAvailable);
        var bitcoin = new NBXplorerNetworkProvider(ChainName.Regtest).GetFromCryptoCode("BTC");
        var serializer = JsonSerializer.Create(bitcoin.Serializer.Settings);
        var method = PaymentTypes.CHAIN.GetPaymentMethodId("BTC");
        var handler = Substitute.For<IPaymentMethodHandler>();
        handler.PaymentMethodId.Returns(method);
        ((IHandler<PaymentMethodId>)handler).Id.Returns(method);
        handler.Serializer.Returns(serializer);
        var reads = 0;
        StoreData? store = null;
        handler.ParsePaymentMethodConfig(Arg.Any<JToken>()).Returns(call =>
        {
            reads++;
            if (failure is not null && (!failSecondRead || reads == 2))
            {
                throw failure switch
                {
                    "json" => new JsonSerializationException("Invalid wallet profile"),
                    "format" => new FormatException("Invalid wallet derivation"),
                    _ => new OperationCanceledException()
                };
            }
            if (failure == "json" && failSecondRead)
            {
                store!.DerivationStrategies = "{";
            }
            return ((JToken)call[0]).ToObject<DerivationSchemeSettings>(serializer)!;
        });
        var dashboard = new NBXplorerDashboard();
        dashboard.Publish(new BTCPayNetwork { CryptoCode = "BTC" }, NBXplorerState.Ready,
            new StatusResult { BitcoinStatus = new() { Capabilities = new() { CanSupportTransactionCheck = true } } }, null!, null!);
        using var controller = context.CreateController(new PaymentMethodHandlerDictionary([handler]), dashboard);
        store = controller.HttpContext.GetNavStoreData()!;
        store.SetStoreBlob(new StoreBlob { PayJoinEnabled = true });
        store.SetPaymentMethodConfig(handler, new DerivationSchemeSettings
        {
            IsHotWallet = true, Source = "ImportedSeed",
            AccountDerivation = bitcoin.DerivationStrategyFactory.CreateDirectDerivationStrategy(
                new ExtKey().Neuter(), new DerivationStrategyOptions { ScriptPubKeyType = ScriptPubKeyType.Segwit })
        });
        if (failure == "json" && !failSecondRead)
        {
            store.DerivationStrategies = "{";
        }

        if (failure == "cancel")
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => controller.Index());
            return;
        }

        var view = Assert.IsType<ViewResult>(await controller.Index());
        var model = Assert.IsType<PayjoinOverviewViewModel>(view.Model).CurrentStore!;
        Assert.Equal(failure is null, model.V1FallbackEffective);
        Assert.Equal(enabled && settingsAvailable && failure is not null && !failSecondRead
            ? PayjoinCheckoutMode.AsyncPayjoin
            : failure is null ? PayjoinCheckoutMode.PayjoinV1 : PayjoinCheckoutMode.StandardBitcoin, model.DefaultCheckoutMode);
        if (!settingsAvailable)
        {
            Assert.Equal("danger", model.Status.Severity);
            Assert.Equal("Unavailable", model.Status.Title);
        }
        else
        {
            Assert.Null(model.HasConfirmedReceiverInputs);
            Assert.Equal(!enabled ? "Disabled" : failSecondRead || failure is null ? "Needs configuration" : "Unavailable", model.Status.Title);
            Assert.Equal(!enabled ? "secondary" : failSecondRead || failure is null ? "danger" : "warning", model.Status.Severity);
            Assert.Contains(failure is null ? "built-in Payjoin v1" : "standard Bitcoin payment", model.Status.Message, StringComparison.Ordinal);
        }
        if (failure != "json")
        {
            Assert.Equal(enabled && settingsAvailable ? 2 : 1, reads);
        }
    }

    [Fact]
    public async Task RetryBridgeRetriesAnEligibleBridgeAndRedirectsToTheOverview()
    {
        using var context = new TestContext();
        var now = DateTimeOffset.UtcNow;
        await context.CreateBridgeAsync("invoice-failed", expiresAt: now.AddHours(1), expectedFinalTransactionId: ExpectedTransactionId);
        await context.BridgeService.MarkFailedAsync("invoice-failed", "reconciliation data problem", CancellationToken.None);
        using var controller = context.CreateController();

        var result = await controller.RetryBridge("invoice-failed");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(UIPayjoinOverviewController.Index), redirect.ActionName);
        Assert.True(controller.TempData.ContainsKey(WellKnownTempData.SuccessMessage));
        var bridge = await context.BridgeService.TryGetByInvoiceIdAsync("invoice-failed", CancellationToken.None);
        Assert.NotNull(bridge);
        Assert.Equal(PayjoinAccountingBridgeStatus.PendingFinalTransaction, bridge!.Status);
    }

    [Fact]
    public async Task RetryBridgeReportsWhenTheBridgeCannotBeRetried()
    {
        using var context = new TestContext();
        var now = DateTimeOffset.UtcNow;
        await context.CreateBridgeAsync("invoice-pending", expiresAt: now.AddHours(1));
        using var controller = context.CreateController();

        var result = await controller.RetryBridge("invoice-pending");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(UIPayjoinOverviewController.Index), redirect.ActionName);
        Assert.True(controller.TempData.ContainsKey(WellKnownTempData.ErrorMessage));
        var bridge = await context.BridgeService.TryGetByInvoiceIdAsync("invoice-pending", CancellationToken.None);
        Assert.NotNull(bridge);
        Assert.Equal(PayjoinAccountingBridgeStatus.PendingFallback, bridge!.Status);
    }

    [Fact]
    public async Task RetryBridgeForbidsUsersWithoutTheModifyStoreSettingsPermission()
    {
        using var context = new TestContext(canModifyStoreSettings: false);
        var now = DateTimeOffset.UtcNow;
        await context.CreateBridgeAsync("invoice-failed", expiresAt: now.AddHours(1));
        await context.BridgeService.MarkFailedAsync("invoice-failed", "reconciliation data problem", CancellationToken.None);
        using var controller = context.CreateController();

        var result = await controller.RetryBridge("invoice-failed");

        Assert.IsType<ForbidResult>(result);
        var bridge = await context.BridgeService.TryGetByInvoiceIdAsync("invoice-failed", CancellationToken.None);
        Assert.NotNull(bridge);
        Assert.Equal(PayjoinAccountingBridgeStatus.Failed, bridge!.Status);
    }

    // Drives the controller against the real bridge and attention services over an in-memory
    // database; only authorization, localization, and store settings are substituted.
    private sealed class TestContext : IDisposable
    {
        private readonly TestPayjoinPluginDbContextFactory _dbContextFactory = new();
        private readonly bool _canModifyStoreSettings;
        private readonly bool _payjoinV2Enabled;
        private readonly bool _registerBitcoinNetwork;
        private readonly bool _settingsAvailable;

        public TestContext(
            bool canModifyStoreSettings = true,
            bool payjoinV2Enabled = true,
            bool registerBitcoinNetwork = false,
            bool settingsAvailable = true)
        {
            _canModifyStoreSettings = canModifyStoreSettings;
            _payjoinV2Enabled = payjoinV2Enabled;
            _registerBitcoinNetwork = registerBitcoinNetwork;
            _settingsAvailable = settingsAvailable;
            BridgeService = new PayjoinAccountingBridgeService(_dbContextFactory, new PayjoinSessionBuildLock());
        }

        public PayjoinAccountingBridgeService BridgeService { get; }

        public Task CreateBridgeAsync(
            string invoiceId,
            DateTimeOffset? expiresAt,
            string? expectedFinalTransactionId = null,
            string storeId = StoreId)
        {
            return _dbContextFactory.SeedBridgeAsync(
                new CreatePayjoinAccountingBridgeRequest(
                    invoiceId,
                    storeId,
                    PayjoinConstants.BitcoinCode,
                    "BTC-BTC",
                    expiresAt,
                    EffectiveInvoiceValueSats: 1000,
                    ExpectedFinalTransactionId: expectedFinalTransactionId),
                CancellationToken.None);
        }

        public UIPayjoinOverviewController CreateController(PaymentMethodHandlerDictionary? handlers = null, NBXplorerDashboard? dashboard = null)
        {
            var storeSettingsRepository = Substitute.For<IPayjoinStoreSettingsRepository>();
            storeSettingsRepository.GetAsync(StoreId).Returns(Task.FromResult<PayjoinStoreSettings?>(_settingsAvailable ? new PayjoinStoreSettings
            {
                PayjoinV2Enabled = _payjoinV2Enabled
            } : null));

            var nbxplorerNetworkProvider = new NBXplorerNetworkProvider(ChainName.Regtest);
            var networks = _registerBitcoinNetwork
                ? new BTCPayNetworkBase[] { CreateBitcoinNetwork(nbxplorerNetworkProvider) }
                : [];
            var networkProvider = new BTCPayNetworkProvider(networks, nbxplorerNetworkProvider, new Logs());

            var availabilityService = new PayjoinAvailabilityService(null!, null!, null!, null!);

            var authorizationService = Substitute.For<IAuthorizationService>();
            authorizationService.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Policies.CanViewStoreSettings)
                .Returns(Task.FromResult(AuthorizationResult.Success()));
            authorizationService.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Policies.CanModifyStoreSettings)
                .Returns(Task.FromResult(_canModifyStoreSettings ? AuthorizationResult.Success() : AuthorizationResult.Failed()));

            var localizer = Substitute.For<IStringLocalizer>();
            localizer[Arg.Any<string>()].Returns(callInfo => new LocalizedString((string)callInfo[0], (string)callInfo[0]));
            localizer[Arg.Any<string>(), Arg.Any<object[]>()].Returns(callInfo => new LocalizedString((string)callInfo[0], (string)callInfo[0]));

            var httpContext = new DefaultHttpContext();
            httpContext.SetNavStoreData(new StoreData { Id = StoreId, StoreName = "Test Store" });

            var controller = new UIPayjoinOverviewController(
                storeSettingsRepository,
                availabilityService,
                handlers!,
                dashboard!,
                networkProvider,
                authorizationService,
                new PayjoinBridgeAttentionService(BridgeService),
                localizer)
            {
                ControllerContext = new ControllerContext { HttpContext = httpContext },
                TempData = new TempDataDictionary(httpContext, Substitute.For<ITempDataProvider>())
            };
            return controller;
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

        public void Dispose()
        {
            using var db = _dbContextFactory.CreateContext();
            db.Database.EnsureDeleted();
        }
    }

    private sealed class TestPayjoinPluginDbContextFactory : PayjoinPluginDbContextFactory
    {
        private static readonly InMemoryDatabaseRoot SharedDatabaseRoot = new();
        private readonly DbContextOptions<PayjoinPluginDbContext> _dbContextOptions;

        public TestPayjoinPluginDbContextFactory()
            : base(Options.Create(new DatabaseOptions
            {
                ConnectionString = "Host=localhost;Database=payjoin-plugin-tests;Username=postgres"
            }))
        {
            var databaseName = $"payjoin-overview-controller-tests-{Guid.NewGuid():N}";
            _dbContextOptions = new DbContextOptionsBuilder<PayjoinPluginDbContext>()
                .UseInMemoryDatabase(databaseName, SharedDatabaseRoot)
                .Options;

            using var db = CreateContext();
            db.Database.EnsureCreated();
        }

        public override PayjoinPluginDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptionsAction = null)
        {
            return new PayjoinPluginDbContext(_dbContextOptions);
        }
    }
}
