using BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Tests;
using NBitpayClient;
using NBXplorer;
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

    [Theory (Skip = "Temporarily skipped will re-enable later") ]
    [InlineData(1, 4)]
    [InlineData(2, 4)]
    [InlineData(4, 4)]
    [InlineData(4, 8)]
    [InlineData(8, 8)]
    [InlineData(1, 16)]
    [InlineData(8, 16)]
    [InlineData(16, 16)]
    [Trait("Integration", "Integration")]
    public async Task ConcurrentReceiverSessionsUnderContentionRespectAvailableReceiverInputs(
        int receiverInputCount,
        int concurrentSessionCount)
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester($"{nameof(ConcurrentReceiverSessionsUnderContentionRespectAvailableReceiverInputs)}-{receiverInputCount}-{concurrentSessionCount}", newDb: true);
        await tester.StartAsync().WaitAsync(cts.Token).ConfigureAwait(true);

        var network = tester.NetworkProvider.GetNetwork<BTCPayNetwork>("BTC");
        Assert.NotNull(network);

        var merchant = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(
            tester,
            network,
            confirmFunding: true,
            initialFundingUtxoCount: receiverInputCount,
            cancellationToken: cts.Token).ConfigureAwait(true);
        var payers = await Task.WhenAll(Enumerable.Range(0, concurrentSessionCount)
            .Select(_ => PayjoinAccountTestHelper.CreateInitializedAccountAsync(tester, network, cancellationToken: cts.Token))).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var invoiceContexts = await Task.WhenAll(Enumerable.Range(0, payers.Length)
            .Select(_ => PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, merchant, cts.Token))).ConfigureAwait(true);
        Assert.All(invoiceContexts, invoiceContext => PayjoinIntegrationTestSupport.AssertPayjoinBip21(invoiceContext.Bip21Response));

        foreach (var invoiceContext in invoiceContexts)
        {
            await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(tester, invoiceContext.InvoiceId, cts.Token).ConfigureAwait(true);
        }

        var paymentTasks = invoiceContexts
            .Zip(payers, (invoiceContext, payer) => PayjoinIntegrationTestSupport.PayInvoiceViaExternalPayjoinPayerAsync(
                tester,
                payer,
                network,
                merchant.StoreId,
                new Uri(invoiceContext.Bip21Response.Bip21, UriKind.Absolute),
                TimeSpan.FromSeconds(5),
                mineBlockAfterBroadcast: false,
                cts.Token))
            .ToArray();

        var paymentResults = await Task.WhenAll(paymentTasks.Select(CapturePaymentOutcomeAsync)).ConfigureAwait(true);
        await MineSingleBlockAsync(tester, network, cts.Token).ConfigureAwait(true);

        var expectedSuccessCount = Math.Min(receiverInputCount, concurrentSessionCount);
        Assert.Equal(expectedSuccessCount, paymentResults.Count(result => result.Succeeded));
        Assert.Equal(paymentResults.Length - expectedSuccessCount, paymentResults.Count(result => !result.Succeeded));

        var successfulIndexes = paymentResults
            .Select((result, index) => new { result, index })
            .Where(x => x.result.Succeeded)
            .Select(x => x.index)
            .ToArray();

        Assert.All(successfulIndexes, successfulIndex => Assert.False(string.IsNullOrWhiteSpace(paymentResults[successfulIndex].TransactionId)));
        Assert.All(paymentResults.Where((_, index) => !successfulIndexes.Contains(index)), result => Assert.NotNull(result.Exception));

        foreach (var successfulInvoiceId in successfulIndexes.Select(index => invoiceContexts[index].InvoiceId))
        {
            await merchant.WaitInvoicePaid(successfulInvoiceId).WaitAsync(cts.Token).ConfigureAwait(true);
        }

        foreach (var invoiceContext in invoiceContexts)
        {
            await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyRemovedAsync(tester, invoiceContext.InvoiceId, cts.Token).ConfigureAwait(true);
        }

        var sessionStore = tester.PayTester.GetService<PayjoinReceiverSessionStore>();
        Assert.DoesNotContain(sessionStore.GetSessions(), session => invoiceContexts.Any(invoiceContext => invoiceContext.InvoiceId == session.InvoiceId));
    }

    [Theory(Skip = "Temporarily skipped will re-enable later")]
    [InlineData(4, 8)]
    [InlineData(4, 16)]
    [InlineData(8, 16)]
    [Trait("Integration", "Integration")]
    public async Task ConcurrentContentionDoesNotPoisonSubsequentPayjoinBatch(
        int receiverInputCount,
        int concurrentSessionCount)
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester($"{nameof(ConcurrentContentionDoesNotPoisonSubsequentPayjoinBatch)}-{receiverInputCount}-{concurrentSessionCount}", newDb: true);
        await tester.StartAsync().WaitAsync(cts.Token).ConfigureAwait(true);

        var network = tester.NetworkProvider.GetNetwork<BTCPayNetwork>("BTC");
        Assert.NotNull(network);

        var merchant = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(
            tester,
            network,
            confirmFunding: true,
            initialFundingUtxoCount: receiverInputCount,
            cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var firstWave = await ExecuteConcurrentContentionWaveAsync(
            tester,
            merchant,
            network,
            receiverInputCount,
            concurrentSessionCount,
            cts.Token).ConfigureAwait(true);

        var receiverOutpointsBeforeSecondWave = await PayjoinIntegrationTestSupport.GetReceiverOutpointsAsync(
            tester,
            merchant.StoreId,
            confirmedOnly: true,
            cts.Token).ConfigureAwait(true);

        Assert.NotEmpty(receiverOutpointsBeforeSecondWave);
        Assert.True(
            receiverOutpointsBeforeSecondWave.Count >= firstWave.SuccessfulPaymentCount,
            $"Expected the receiver to retain enough confirmed coins for the second wave. Available after first wave: {receiverOutpointsBeforeSecondWave.Count}, first-wave successes: {firstWave.SuccessfulPaymentCount}.");

        var secondWave = await ExecuteConcurrentContentionWaveAsync(
            tester,
            merchant,
            network,
            receiverOutpointsBeforeSecondWave.Count,
            concurrentSessionCount,
            cts.Token).ConfigureAwait(true);

        Assert.Equal(
            Math.Min(receiverOutpointsBeforeSecondWave.Count, concurrentSessionCount),
            secondWave.SuccessfulPaymentCount);
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

    private static async Task MineSingleBlockAsync(ServerTester tester, BTCPayNetwork network, CancellationToken cancellationToken)
    {
        var explorerClient = tester.PayTester.GetService<ExplorerClientProvider>().GetExplorerClient(network);
        var rpc = explorerClient.RPCClient ?? tester.ExplorerNode;
        var rewardAddress = await rpc.GetNewAddressAsync(cancellationToken).ConfigureAwait(true);
        await rpc.GenerateToAddressAsync(1, rewardAddress, cancellationToken).ConfigureAwait(true);
    }

    private static async Task<ContentionWaveResult> ExecuteConcurrentContentionWaveAsync(
        ServerTester tester,
        TestAccount merchant,
        BTCPayNetwork network,
        int availableReceiverInputCount,
        int concurrentSessionCount,
        CancellationToken cancellationToken)
    {
        var payers = await Task.WhenAll(Enumerable.Range(0, concurrentSessionCount)
            .Select(_ => PayjoinAccountTestHelper.CreateInitializedAccountAsync(tester, network, cancellationToken: cancellationToken))).ConfigureAwait(true);

        var invoiceContexts = await Task.WhenAll(Enumerable.Range(0, concurrentSessionCount)
            .Select(_ => PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, merchant, cancellationToken))).ConfigureAwait(true);
        Assert.All(invoiceContexts, invoiceContext => PayjoinIntegrationTestSupport.AssertPayjoinBip21(invoiceContext.Bip21Response));

        foreach (var invoiceContext in invoiceContexts)
        {
            await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(tester, invoiceContext.InvoiceId, cancellationToken).ConfigureAwait(true);
        }

        var paymentTasks = invoiceContexts
            .Zip(payers, (invoiceContext, payer) => PayjoinIntegrationTestSupport.PayInvoiceViaExternalPayjoinPayerAsync(
                tester,
                payer,
                network,
                merchant.StoreId,
                new Uri(invoiceContext.Bip21Response.Bip21, UriKind.Absolute),
                TimeSpan.FromSeconds(5),
                mineBlockAfterBroadcast: false,
                cancellationToken))
            .ToArray();

        var paymentResults = await Task.WhenAll(paymentTasks.Select(CapturePaymentOutcomeAsync)).ConfigureAwait(true);
        await MineSingleBlockAsync(tester, network, cancellationToken).ConfigureAwait(true);

        var expectedSuccessCount = Math.Min(availableReceiverInputCount, concurrentSessionCount);
        Assert.Equal(expectedSuccessCount, paymentResults.Count(result => result.Succeeded));
        Assert.Equal(paymentResults.Length - expectedSuccessCount, paymentResults.Count(result => !result.Succeeded));

        var successfulIndexes = paymentResults
            .Select((result, index) => new { result, index })
            .Where(x => x.result.Succeeded)
            .Select(x => x.index)
            .ToArray();

        Assert.All(successfulIndexes, successfulIndex => Assert.False(string.IsNullOrWhiteSpace(paymentResults[successfulIndex].TransactionId)));
        Assert.All(paymentResults.Where((_, index) => !successfulIndexes.Contains(index)), result => Assert.NotNull(result.Exception));

        foreach (var successfulInvoiceId in successfulIndexes.Select(index => invoiceContexts[index].InvoiceId))
        {
            await merchant.WaitInvoicePaid(successfulInvoiceId).WaitAsync(cancellationToken).ConfigureAwait(true);
        }

        foreach (var invoiceContext in invoiceContexts)
        {
            await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyRemovedAsync(tester, invoiceContext.InvoiceId, cancellationToken).ConfigureAwait(true);
        }

        var invoiceIds = invoiceContexts.Select(invoiceContext => invoiceContext.InvoiceId).ToArray();
        var sessionStore = tester.PayTester.GetService<PayjoinReceiverSessionStore>();
        Assert.DoesNotContain(sessionStore.GetSessions(), session => invoiceIds.Contains(session.InvoiceId, StringComparer.Ordinal));

        return new ContentionWaveResult(expectedSuccessCount);
    }

    private sealed record PaymentOutcome(bool Succeeded, string? TransactionId, Exception? Exception);

    private sealed record ContentionWaveResult(int SuccessfulPaymentCount);
}
