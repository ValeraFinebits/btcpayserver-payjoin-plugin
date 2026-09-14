using BTCPayServer.Abstractions;
using BTCPayServer.HostedServices;
using BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using BTCPayServer.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using SystemUri = System.Uri;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class PayjoinSenderTickConcurrencyIntegrationTests : UnitTestBase
{
    private static readonly RequestBaseUrl TestRequestBaseUrl = RequestBaseUrl.FromUrl(new SystemUri("http://127.0.0.1/"));

    public PayjoinSenderTickConcurrencyIntegrationTests(ITestOutputHelper helper) : base(helper)
    {
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task OneSlowRelayRequestDoesNotDelayTheOtherSessions()
    {
        // A relay long-poll can hold a request open for a long time. Sessions advance
        // independently, so the tick must start every session's work rather than await them one
        // by one; a coordinating relay proves it by refusing to release the first request until
        // the second has arrived. A sequential tick deadlocks here and fails by timeout.
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);
        var payer = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(tester, context.Network, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);
        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, payer.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        // The server's own poller must not drive these sessions; this test ticks by hand.
        var poller = tester.PayTester.ServiceProvider
            .GetServices<IHostedService>()
            .OfType<PayjoinSenderPoller>()
            .Single();
        await poller.StopAsync(cts.Token).ConfigureAwait(true);

        var (firstInvoiceId, firstBip21) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.Merchant, cts.Token).ConfigureAwait(true);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(tester, firstInvoiceId, cts.Token).ConfigureAwait(true);
        var (secondInvoiceId, secondBip21) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.Merchant, cts.Token).ConfigureAwait(true);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(tester, secondInvoiceId, cts.Token).ConfigureAwait(true);

        var senderService = tester.PayTester.GetService<PayjoinSenderService>();
        var first = await senderService.StartAsync(payer.StoreId, firstBip21.Bip21, feeRateSatPerVb: 5m, TestRequestBaseUrl, selectedInputs: null, cts.Token).ConfigureAwait(true);
        Assert.True(first.Success, first.Error);
        var second = await senderService.StartAsync(payer.StoreId, secondBip21.Bip21, feeRateSatPerVb: 5m, TestRequestBaseUrl, selectedInputs: null, cts.Token).ConfigureAwait(true);
        Assert.True(second.Success, second.Error);

        var provider = tester.PayTester.ServiceProvider;
        var coordinatingRelay = new CoordinatingRelaySender();
        var processor = new PayjoinSenderSessionProcessor(
            provider.GetRequiredService<PayjoinSenderSessionStore>(),
            coordinatingRelay,
            provider.GetRequiredService<BTCPayNetworkProvider>(),
            provider.GetRequiredService<StoreRepository>(),
            provider.GetRequiredService<PaymentMethodHandlerDictionary>(),
            provider.GetRequiredService<BTCPayServer.ExplorerClientProvider>(),
            provider.GetRequiredService<PendingTransactionService>(),
            provider.GetRequiredService<PayjoinSenderSignatureHandler>(),
            NullLogger<PayjoinSenderSessionProcessor>.Instance);

        await processor.ProcessTickAsync(cts.Token).WaitAsync(cts.Token).ConfigureAwait(true);

        Assert.True(coordinatingRelay.SecondRequestArrivedWhileFirstWasHeld);
    }

    /// <summary>
    /// Holds the first request until the second arrives, then fails both transiently. If the
    /// second request never comes, the whole test times out, which is the sequential-processing
    /// failure mode this exists to catch.
    /// </summary>
    private sealed class CoordinatingRelaySender : IPayjoinReceiverRelayRequestSender
    {
        private readonly TaskCompletionSource _secondRequestArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requestCount;

        public bool SecondRequestArrivedWhileFirstWasHeld { get; private set; }

        public async Task<(byte[] ResponseBody, TRequestContext RequestContext)> SendAsync<TRequestContext>(
            string storeId,
            string sessionId,
            Func<string, TRequestContext> buildRequest,
            Func<TRequestContext, (SystemUri Url, string ContentType, byte[] Body)> describeRequest,
            CancellationToken cancellationToken)
            where TRequestContext : IDisposable
        {
            var requestNumber = Interlocked.Increment(ref _requestCount);
            if (requestNumber == 1)
            {
                await _secondRequestArrived.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                SecondRequestArrivedWhileFirstWasHeld = true;
            }
            else
            {
                _secondRequestArrived.TrySetResult();
            }

            throw new System.Net.Http.HttpRequestException("coordinating relay: transient by design");
        }
    }
}
