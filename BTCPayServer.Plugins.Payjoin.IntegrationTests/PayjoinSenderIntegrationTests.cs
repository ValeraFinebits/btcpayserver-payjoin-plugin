using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Tests;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class PayjoinSenderIntegrationTests : UnitTestBase
{
    public PayjoinSenderIntegrationTests(ITestOutputHelper helper) : base(helper)
    {
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task StoreHotWalletPaysPluginInvoiceThroughAsyncSenderSession()
    {
        // The full wallet-side loop inside one BTCPay instance: the merchant store receives
        // through the plugin's receiver sessions, and the payer store pays through the new
        // async sender session. Both background pollers run as hosted services, so after
        // StartAsync the payjoin completes with no further calls from the test.
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);
        var payer = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(tester, context.Network, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);
        // The payer store needs the OHTTP relay configuration; the sender session posts and
        // polls through the store's configured relays.
        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, payer.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var receiverOutpointsBeforePayment = await PayjoinIntegrationTestSupport.GetReceiverOutpointsAsync(
            tester,
            context.Merchant.StoreId,
            confirmedOnly: true,
            cts.Token).ConfigureAwait(true);
        Assert.NotEmpty(receiverOutpointsBeforePayment);

        var (invoiceId, bip21Response) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.Merchant, cts.Token).ConfigureAwait(true);
        PayjoinIntegrationTestSupport.AssertPayjoinBip21(bip21Response);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);

        var senderService = tester.PayTester.GetService<PayjoinSenderService>();
        var startResult = await senderService.StartAsync(payer.StoreId, bip21Response.Bip21, feeRateSatPerVb: 5m, cts.Token).ConfigureAwait(true);
        Assert.True(startResult.Success, startResult.Error);
        Assert.NotNull(startResult.SenderSessionId);
        Assert.NotNull(startResult.OriginalTransactionId);

        // A duplicate submission of the same URI must be refused while the session runs.
        var duplicate = await senderService.StartAsync(payer.StoreId, bip21Response.Bip21, feeRateSatPerVb: 5m, cts.Token).ConfigureAwait(true);
        Assert.False(duplicate.Success);

        var senderSessionStore = tester.PayTester.GetService<PayjoinSenderSessionStore>();
        PayjoinSenderSessionState? completedSession = null;
        await AsyncPolling.WaitUntilAsync(
            PayjoinIntegrationTestSupport.TestTimeout,
            TimeSpan.FromSeconds(1),
            _ =>
            {
                if (senderSessionStore.TryGetSession(startResult.SenderSessionId!, out var session) &&
                    session!.Status != PayjoinSenderSessionStatus.Pending)
                {
                    completedSession = session;
                    return Task.FromResult(true);
                }

                return Task.FromResult(false);
            },
            shouldRetry: null,
            _ => $"Sender session {startResult.SenderSessionId} did not complete. Last status: {(senderSessionStore.TryGetSession(startResult.SenderSessionId!, out var last) ? last!.Status.ToString() : "missing")}, failure: {last?.FailureMessage}",
            cts.Token).ConfigureAwait(true);

        Assert.NotNull(completedSession);
        Assert.Equal(PayjoinSenderSessionStatus.CompletedPayjoin, completedSession!.Status);
        Assert.NotNull(completedSession.BroadcastTransactionId);
        // A broadcast equal to the original would mean the fallback ran instead of the payjoin.
        Assert.NotEqual(startResult.OriginalTransactionId, completedSession.BroadcastTransactionId);

        var rewardAddress = await tester.ExplorerNode.GetNewAddressAsync(cts.Token).ConfigureAwait(true);
        await tester.ExplorerNode.GenerateToAddressAsync(1, rewardAddress, cts.Token).ConfigureAwait(true);

        await context.Merchant.WaitInvoicePaid(invoiceId).WaitAsync(cts.Token).ConfigureAwait(true);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyRemovedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);

        var bestBlock = await tester.ExplorerNode.GetBestBlockHashAsync(cts.Token).ConfigureAwait(true);
        var broadcastTransaction = await tester.ExplorerNode
            .GetRawTransactionAsync(uint256.Parse(completedSession.BroadcastTransactionId!), bestBlock, cancellationToken: cts.Token)
            .ConfigureAwait(true);

        // The defining property of the payjoin: the receiver contributed one of its own
        // confirmed inputs to the sender's transaction.
        Assert.Contains(
            broadcastTransaction.Inputs,
            input => receiverOutpointsBeforePayment.Contains(input.PrevOut.ToString()));
    }
}
