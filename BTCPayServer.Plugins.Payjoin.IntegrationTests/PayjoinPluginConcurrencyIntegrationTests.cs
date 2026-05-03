using BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Tests;
using NBitpayClient;
using Xunit;
using Xunit.Abstractions;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class PayjoinPluginConcurrencyIntegrationTests : UnitTestBase
{
    public PayjoinPluginConcurrencyIntegrationTests(ITestOutputHelper helper) : base(helper)
    {
    }

    [Fact
    (Skip = "Manual Docker-backed integration test. Remove Skip to run it explicitly.")
    ]
    [Trait("Integration", "Integration")]
    public async Task ConcurrentGetBip21RequestsAreIdempotentForSameInvoice()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.User.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var invoice = await context.User.BitPay.CreateInvoiceAsync(new Invoice
        {
            Price = 0.1m,
            Currency = "BTC",
            FullNotifications = true
        }).WaitAsync(cts.Token).ConfigureAwait(true);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => PayjoinIntegrationTestSupport.GetBip21Async(tester, invoice.Id, cts.Token))).ConfigureAwait(true);

        Assert.All(responses, PayjoinIntegrationTestSupport.AssertPayjoinBip21);
        Assert.Single(responses.Select(response => response.Bip21).Distinct(StringComparer.Ordinal));

        var sessionStore = tester.PayTester.GetService<PayjoinReceiverSessionStore>();
        Assert.Single(sessionStore.GetSessions(), s => s.InvoiceId == invoice.Id);
    }
}
