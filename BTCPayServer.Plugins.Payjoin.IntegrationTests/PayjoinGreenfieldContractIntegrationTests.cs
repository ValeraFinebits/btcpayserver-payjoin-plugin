using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;
using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Tests;
using NBitpayClient;
using Newtonsoft.Json.Linq;
using System.Net.Http.Headers;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class PayjoinGreenfieldContractIntegrationTests : UnitTestBase
{
    public PayjoinGreenfieldContractIntegrationTests(ITestOutputHelper helper) : base(helper)
    {
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task GreenfieldPaymentUrlEndpointServesTheDocumentedStatusContractOverHttp()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var invoice = await CreateInvoiceAsync(context.Merchant, cts.Token).ConfigureAwait(true);
        var payload = await GetGreenfieldPaymentUrlJsonAsync(tester, context.Merchant, invoice.Id, cts.Token).ConfigureAwait(true);

        Assert.Equal("Active", payload["status"]!.Value<string>());
        Assert.False(payload["retryable"]!.Value<bool>());
        Assert.True(payload.TryGetValue("unavailableReason", out var unavailableReason));
        Assert.Equal(JTokenType.Null, unavailableReason!.Type);

        var bip21 = payload["bip21"]!.Value<string>()!;
        Assert.Contains("pj=", bip21, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(invoice.BitcoinAddress, bip21, StringComparison.Ordinal);
        Assert.Contains("amount=0.1", bip21, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task GreenfieldPaymentUrlEndpointServesTheGranularStatusAndReasonOverHttp()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.DisablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var invoice = await CreateInvoiceAsync(context.Merchant, cts.Token).ConfigureAwait(true);
        var payload = await GetGreenfieldPaymentUrlJsonAsync(tester, context.Merchant, invoice.Id, cts.Token).ConfigureAwait(true);

        Assert.Equal("DisabledByStore", payload["status"]!.Value<string>());
        Assert.False(payload["retryable"]!.Value<bool>());
        Assert.Equal(
            PayjoinUnavailableReasons.DisabledByStoreSettings,
            payload["unavailableReason"]!.Value<string>());
        Assert.DoesNotContain("pj=", payload["bip21"]!.Value<string>()!, StringComparison.OrdinalIgnoreCase);
    }

    private static Task<Invoice> CreateInvoiceAsync(TestAccount merchant, CancellationToken cancellationToken)
    {
        return merchant.BitPay.CreateInvoiceAsync(new Invoice
        {
            Price = 0.1m,
            Currency = PayjoinConstants.BitcoinCode,
            FullNotifications = true
        }).WaitAsync(cancellationToken);
    }

    private static async Task<JObject> GetGreenfieldPaymentUrlJsonAsync(
        ServerTester tester,
        TestAccount merchant,
        string invoiceId,
        CancellationToken cancellationToken)
    {
        var owner = await merchant.CreateClient().ConfigureAwait(true);
        var apiKey = await owner.CreateAPIKey(new CreateApiKeyRequest
        {
            Label = "payjoin greenfield contract test",
            Permissions = [Permission.Parse($"{Policies.CanViewInvoices}:{merchant.StoreId}")]
        }, cancellationToken).ConfigureAwait(true);

        var endpoint = new Uri($"api/v1/stores/{merchant.StoreId}/invoices/{invoiceId}/payjoin/payment-url", UriKind.Relative);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("token", apiKey.ApiKey);

        using var response = await tester.PayTester.HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(true);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);
        return JObject.Parse(body);
    }
}
