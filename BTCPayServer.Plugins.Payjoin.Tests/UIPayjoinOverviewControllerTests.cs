using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Logging;
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

        public TestContext(
            bool canModifyStoreSettings = true,
            bool payjoinV2Enabled = true,
            bool registerBitcoinNetwork = false)
        {
            _canModifyStoreSettings = canModifyStoreSettings;
            _payjoinV2Enabled = payjoinV2Enabled;
            _registerBitcoinNetwork = registerBitcoinNetwork;
            BridgeService = new PayjoinAccountingBridgeService(_dbContextFactory, new PostgresPayjoinUniqueConstraintViolationDetector(), new PayjoinSessionBuildLock());
        }

        public PayjoinAccountingBridgeService BridgeService { get; }

        public Task CreateBridgeAsync(
            string invoiceId,
            DateTimeOffset? expiresAt,
            string? expectedFinalTransactionId = null,
            string storeId = StoreId)
        {
            return BridgeService.CreateOrGetAsync(
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

        public UIPayjoinOverviewController CreateController()
        {
            var storeSettingsRepository = Substitute.For<IPayjoinStoreSettingsRepository>();
            storeSettingsRepository.GetAsync(StoreId).Returns(Task.FromResult(new PayjoinStoreSettings
            {
                PayjoinV2Enabled = _payjoinV2Enabled
            }));

            var nbxplorerNetworkProvider = new NBXplorerNetworkProvider(ChainName.Regtest);
            var networks = _registerBitcoinNetwork
                ? new BTCPayNetworkBase[] { CreateBitcoinNetwork(nbxplorerNetworkProvider) }
                : [];
            var networkProvider = new BTCPayNetworkProvider(networks, nbxplorerNetworkProvider, new Logs());

            var availabilityService = new PayjoinAvailabilityService(null!, null!, null!);

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
                null!,
                null!,
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
