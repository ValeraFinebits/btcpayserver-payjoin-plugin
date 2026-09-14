using BTCPayServer.Data;
using BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;
using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services.Stores;
using BTCPayServer.Tests;
using NBitpayClient;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class PayjoinMissingStoreSettingsIntegrationTests : UnitTestBase
{
    public PayjoinMissingStoreSettingsIntegrationTests(ITestOutputHelper helper) : base(helper)
    {
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task UnreadableStoreSettingsReportTemporarilyUnavailable()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var invoice = await context.Merchant.BitPay.CreateInvoiceAsync(new Invoice
        {
            Price = 0.1m,
            Currency = PayjoinConstants.BitcoinCode,
            FullNotifications = true
        }).WaitAsync(cts.Token).ConfigureAwait(true);

        await DamagePayjoinSettingsAsync(tester, context.Merchant.StoreId, cts.Token).ConfigureAwait(true);

        var service = tester.PayTester.GetService<IPayjoinInvoicePaymentUrlService>();
        var response = await service.GetInvoicePaymentUrlAsync(invoice.Id, cts.Token).ConfigureAwait(true);

        Assert.NotNull(response);
        Assert.Equal(PayjoinAvailabilityStatus.TemporarilyUnavailable, response!.Status);
        Assert.Equal(PayjoinUnavailableReasons.StoreSettingsUnavailable, response.UnavailableReason);
        Assert.DoesNotContain("pj=", response.Bip21, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task DamagePayjoinSettingsAsync(ServerTester tester, string storeId, CancellationToken cancellationToken)
    {
        var storeRepository = tester.PayTester.GetService<StoreRepository>();
        var store = await storeRepository.FindStore(storeId).WaitAsync(cancellationToken).ConfigureAwait(true);
        Assert.NotNull(store);

        var blob = store!.GetStoreBlob();
        blob.AdditionalData ??= new JObject();
        blob.AdditionalData["payjoin.settings"] = new JArray("not", "a", "settings", "object");
        store.SetStoreBlob(blob);
        await storeRepository.UpdateStore(store).WaitAsync(cancellationToken).ConfigureAwait(true);
    }
}
