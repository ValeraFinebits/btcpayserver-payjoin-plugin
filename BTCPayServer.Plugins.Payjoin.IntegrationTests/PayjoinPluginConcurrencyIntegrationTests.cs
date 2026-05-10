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

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task ConcurrentGetBip21RequestsAreIdempotentForSameInvoice()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var invoice = await context.Merchant.BitPay.CreateInvoiceAsync(new Invoice
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

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task ConcurrentReceiverSessionsAllowOnlyOneSuccessfulPaymentWhenSingleReceiverInputExists()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        await tester.StartAsync().WaitAsync(cts.Token).ConfigureAwait(true);

        var network = tester.NetworkProvider.GetNetwork<BTCPayNetwork>("BTC");
        Assert.NotNull(network);

        var merchant = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(
            tester,
            network,
            confirmFunding: true,
            initialFundingUtxoCount: 1,
            cancellationToken: cts.Token).ConfigureAwait(true);
        var payerOne = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(tester, network, cancellationToken: cts.Token).ConfigureAwait(true);
        var payerTwo = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(tester, network, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var (invoiceIdOne, bip21ResponseOne) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, merchant, cts.Token).ConfigureAwait(true);
        var (invoiceIdTwo, bip21ResponseTwo) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, merchant, cts.Token).ConfigureAwait(true);

        PayjoinIntegrationTestSupport.AssertPayjoinBip21(bip21ResponseOne);
        PayjoinIntegrationTestSupport.AssertPayjoinBip21(bip21ResponseTwo);

        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(tester, invoiceIdOne, cts.Token).ConfigureAwait(true);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(tester, invoiceIdTwo, cts.Token).ConfigureAwait(true);

        var paymentTaskOne = PayjoinIntegrationTestSupport.PayInvoiceViaExternalPayjoinPayerAsync(
            tester,
            payerOne,
            network,
            merchant.StoreId,
            new Uri(bip21ResponseOne.Bip21, UriKind.Absolute),
            cts.Token);
        var paymentTaskTwo = PayjoinIntegrationTestSupport.PayInvoiceViaExternalPayjoinPayerAsync(
            tester,
            payerTwo,
            network,
            merchant.StoreId,
            new Uri(bip21ResponseTwo.Bip21, UriKind.Absolute),
            cts.Token);

        var paymentResults = await Task.WhenAll(
            CapturePaymentOutcomeAsync(paymentTaskOne),
            CapturePaymentOutcomeAsync(paymentTaskTwo)).ConfigureAwait(true);

        Assert.Single(paymentResults, result => result.Succeeded);
        Assert.Single(paymentResults, result => !result.Succeeded);

        var successfulIndex = paymentResults[0].Succeeded ? 0 : 1;
        var successfulInvoiceId = successfulIndex == 0 ? invoiceIdOne : invoiceIdTwo;
        var failedInvoiceId = successfulIndex == 0 ? invoiceIdTwo : invoiceIdOne;
        var successfulTransactionId = paymentResults[successfulIndex].TransactionId;
        var failedException = paymentResults[successfulIndex == 0 ? 1 : 0].Exception;

        Assert.False(string.IsNullOrWhiteSpace(successfulTransactionId));
        Assert.NotNull(failedException);

        await merchant.WaitInvoicePaid(successfulInvoiceId).WaitAsync(cts.Token).ConfigureAwait(true);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyRemovedAsync(tester, successfulInvoiceId, cts.Token).ConfigureAwait(true);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyRemovedAsync(tester, failedInvoiceId, cts.Token).ConfigureAwait(true);
    }

    private static Task<PaymentOutcome> CapturePaymentOutcomeAsync(Task<string> paymentTask)
    {
        return paymentTask.ContinueWith(completedTask =>
        {
            return completedTask.Status switch
            {
                TaskStatus.RanToCompletion => new PaymentOutcome(true, completedTask.Result, null),
                TaskStatus.Faulted => new PaymentOutcome(false, null, completedTask.Exception?.GetBaseException() ?? new InvalidOperationException("Competing payment failed without an observable exception.")),
                TaskStatus.Canceled => new PaymentOutcome(false, null, new TaskCanceledException(completedTask)),
                _ => new PaymentOutcome(false, null, new InvalidOperationException($"Unexpected payment task status '{completedTask.Status}'."))
            };
        }, TaskScheduler.Default);
    }

    private sealed record PaymentOutcome(bool Succeeded, string? TransactionId, Exception? Exception);
}
