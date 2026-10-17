using BTCPayServer.Logging;
using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Plugins.Payjoin.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NBXplorer;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class PayjoinUriSessionServiceStatusTests
{
    private const string Destination = "bcrt1qexampledestination";
    private static readonly Uri[] SampleUrls = [new("https://configured.example/endpoint")];

    private static PayjoinUriSessionService CreateService(IPayjoinAccountingBridgeService bridges, PayjoinReceiverSessionStore? store = null)
    {
        return new PayjoinUriSessionService(
            CreateNetworkProvider(),
            store!,
            null!,
            null!,
            null!,
            bridges,
            null!,
            NullLogger<PayjoinUriSessionService>.Instance);
    }

    private static async Task<PayjoinUriResult> BuildAsync(
        PayjoinStoreSettings? storeSettings,
        bool enablePayjoin = true,
        decimal due = 0.1m)
    {
        using var fixture = new SessionStoreFixture();
        return await CreateService(fixture.CreateBridgeService()).BuildAsync(
            PayjoinConstants.BitcoinCode,
            Destination,
            due,
            storeSettings,
            enablePayjoin,
            "invoice-1",
            "store-1",
            DateTimeOffset.UtcNow.AddHours(1),
            TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    private static PayjoinStoreSettings CreateSettings(Uri[]? directoryUrls = null, Uri[]? ohttpRelayUrls = null)
    {
        return new PayjoinStoreSettings
        {
            PayjoinV2Enabled = true,
            DirectoryUrls = directoryUrls ?? SampleUrls,
            OhttpRelayUrls = ohttpRelayUrls ?? SampleUrls
        };
    }

    private static void AssertPlainBip21Fallback(PayjoinUriResult result, PayjoinAvailabilityStatus expectedStatus, string expectedReason)
    {
        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedReason, result.Reason);
        Assert.DoesNotContain("pj=", result.PaymentUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DisabledStoreSettingMapsToDisabledByStore()
    {
        var result = await BuildAsync(CreateSettings(), enablePayjoin: false);

        AssertPlainBip21Fallback(result, PayjoinAvailabilityStatus.DisabledByStore, PayjoinUnavailableReasons.DisabledByStoreSettings);
    }

    [Fact]
    public async Task UnreadableStoreSettingsAreTemporaryButNotRetryable()
    {
        var result = await BuildAsync(storeSettings: null);

        AssertPlainBip21Fallback(result, PayjoinAvailabilityStatus.TemporarilyUnavailable, PayjoinUnavailableReasons.StoreSettingsUnavailable);
        Assert.False(result.Retryable);
    }

    [Fact]
    public async Task MissingDirectoryUrlsMapToMerchantRequirementsUnmet()
    {
        var result = await BuildAsync(CreateSettings(directoryUrls: []));

        AssertPlainBip21Fallback(result, PayjoinAvailabilityStatus.MerchantRequirementsUnmet, PayjoinUnavailableReasons.DirectoryUrlsMissing);
    }

    [Fact]
    public async Task MissingOhttpRelayUrlsMapToMerchantRequirementsUnmet()
    {
        var result = await BuildAsync(CreateSettings(ohttpRelayUrls: []));

        AssertPlainBip21Fallback(result, PayjoinAvailabilityStatus.MerchantRequirementsUnmet, PayjoinUnavailableReasons.OhttpRelayUrlsMissing);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.5)]
    public async Task NonPositiveDueMapsToInvoiceNotPayable(decimal due)
    {
        var result = await BuildAsync(CreateSettings(), due: due);

        AssertPlainBip21Fallback(result, PayjoinAvailabilityStatus.InvoiceNotPayable, PayjoinUnavailableReasons.InvoiceAmountNotPositive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedBridgeBlocksCheckoutAndCachedUriWithOrWithoutHeldSession(bool heldSession)
    {
        using var fixture = new RelationalPluginTestContext();
        var store = fixture.CreateStore();
        var bridges = fixture.CreateBridgeService();
        await fixture.SeedBridgeAsync(new CreatePayjoinAccountingBridgeRequest("invoice-1", "store-1", "BTC", "BTC-CHAIN",
            DateTimeOffset.UtcNow.AddHours(1), EffectiveInvoiceValueSats: 10_000_000), CancellationToken.None);
        const string cachedUri = "bitcoin:bcrt1qexampledestination?amount=0.1&pj=https://example.com/session";
        if (heldSession)
        {
            store.GetOrCreateSession("invoice-1", Destination, "store-1", DateTimeOffset.UtcNow.AddHours(1), ["bootstrap", "original", "proposal"]);
            store.StorePayjoinUri("invoice-1", Destination, cachedUri);
        }
        await bridges.MarkFailedAsync("invoice-1", "opaque reason without policy keywords", CancellationToken.None);
        var before = await bridges.TryGetByInvoiceIdAsync("invoice-1", CancellationToken.None);
        for (var restart = 0; restart < 2; restart++)
        {
            var reloadedStore = fixture.CreateStore();
            var service = CreateService(fixture.CreateBridgeService(), reloadedStore);
            var result = await service.BuildAsync("BTC", Destination, 0.1m, CreateSettings(), true, "invoice-1", "store-1",
                DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);
            AssertPlainBip21Fallback(result, PayjoinAvailabilityStatus.TemporarilyUnavailable, PayjoinUriSessionService.ManualRecoveryRequiredReason);
            Assert.False(result.Retryable);
            Assert.Null(reloadedStore.GetServablePayjoinUri("invoice-1", Destination));
            Assert.Equal(heldSession, reloadedStore.TryGetSession("invoice-1", out var held));
            if (heldSession)
            {
                Assert.Equal(cachedUri, held!.PayjoinUri);
                Assert.Equal(new[] { "bootstrap", "original", "proposal" }, held.GetEvents());
                Assert.False(reloadedStore.RemoveSession("invoice-1"));
                Assert.False(reloadedStore.TryRemoveSessionUnlessNegotiating("invoice-1"));
            }
        }
        Assert.Equal(before, await bridges.TryGetByInvoiceIdAsync("invoice-1", CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedPendingAttemptReportsWaitingWithoutChangingUriOrRetryPolicy(bool finalTransactionPending)
    {
        using var fixture = new RelationalPluginTestContext();
        var store = fixture.CreateStore();
        await fixture.SeedBridgeAsync(new CreatePayjoinAccountingBridgeRequest("invoice-1", "store-1", "BTC", "BTC-CHAIN",
            DateTimeOffset.UtcNow.AddHours(1), EffectiveInvoiceValueSats: 10_000_000, SettlementScript: "51"), CancellationToken.None);
        using (var db = fixture.CreateDbContext())
        {
            db.AccountingBridges.Single().Status = finalTransactionPending ? PayjoinAccountingBridgeStatus.PendingFinalTransaction : PayjoinAccountingBridgeStatus.PendingFallback;
            db.SaveChanges();
        }
        var session = store.GetOrCreateSession("invoice-1", Destination, "store-1", DateTimeOffset.UtcNow.AddHours(1), ["retained history"]);
        store.StorePayjoinUri("invoice-1", Destination, "bitcoin:cached-exact-uri", session.SessionInstanceId);
        var service = CreateService(fixture.CreateBridgeService(), store);
        var result = await service.BuildAsync("BTC", Destination, 0.1m, CreateSettings(), true, "invoice-1", "store-1",
            DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);
        AssertPlainBip21Fallback(result, PayjoinAvailabilityStatus.TemporarilyUnavailable,
            "the current Payjoin attempt is being processed or awaiting settlement");
        Assert.False(result.Retryable);
        Assert.Null(store.GetServablePayjoinUri("invoice-1", Destination));
        Assert.Equal(session.SessionInstanceId, store.Owner("invoice-1").SessionInstanceId);
        Assert.Null(await fixture.CreateBridgeService().TryRetryAsync("invoice-1", "store-1", DateTimeOffset.UtcNow, CancellationToken.None));
    }

    private static BTCPayNetworkProvider CreateNetworkProvider()
    {
        var nbxplorerNetworkProvider = new NBXplorerNetworkProvider(ChainName.Regtest);
        var network = new BTCPayNetwork
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

        return new BTCPayNetworkProvider([network], nbxplorerNetworkProvider, new Logs());
    }
}
