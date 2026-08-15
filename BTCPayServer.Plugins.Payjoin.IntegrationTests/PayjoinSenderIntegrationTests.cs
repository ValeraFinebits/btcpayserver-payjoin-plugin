using BTCPayServer.Abstractions;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
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
    // The sender records the base URL of the request that started it, so a background poller
    // can still build the links a pending transaction needs. Tests have no HttpContext.
    private static readonly RequestBaseUrl TestRequestBaseUrl = RequestBaseUrl.FromUrl(new Uri("http://127.0.0.1/"));

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
        var startResult = await senderService.StartAsync(payer.StoreId, bip21Response.Bip21, feeRateSatPerVb: 5m, TestRequestBaseUrl, cts.Token).ConfigureAwait(true);
        Assert.True(startResult.Success, startResult.Error);
        Assert.NotNull(startResult.SenderSessionId);
        Assert.NotNull(startResult.OriginalTransactionId);

        // A duplicate submission of the same URI must be refused while the session runs.
        var duplicate = await senderService.StartAsync(payer.StoreId, bip21Response.Bip21, feeRateSatPerVb: 5m, TestRequestBaseUrl, cts.Token).ConfigureAwait(true);
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

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task StoreColdWalletPaysPluginInvoiceAfterTwoOffServerSignatures()
    {
        // The same loop with a wallet the server cannot sign for. The payer store holds a
        // watch-only derivation scheme, so the session parks on a BTCPay pending transaction
        // twice: once for the original, and once more for the receiver's proposal, which is a
        // different transaction. The test plays the operator and signs both off the server.
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);
        var payer = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(
            tester,
            context.Network,
            serverHoldsKeys: false,
            cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);
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
        var startResult = await senderService.StartAsync(payer.StoreId, bip21Response.Bip21, feeRateSatPerVb: 5m, TestRequestBaseUrl, cts.Token).ConfigureAwait(true);
        Assert.True(startResult.Success, startResult.Error);
        // Nothing was signed on the server, so the session waits instead of starting.
        Assert.NotNull(startResult.PendingTransactionId);

        var senderSessionStore = tester.PayTester.GetService<PayjoinSenderSessionStore>();
        var senderSessionId = startResult.SenderSessionId!;
        Assert.True(senderSessionStore.TryGetSession(senderSessionId, out var awaitingSession));
        Assert.Equal(PayjoinSenderSessionStatus.AwaitingSignature, awaitingSession!.Status);
        Assert.Empty(awaitingSession.Events);

        // The first signature is the original. It starts the session, and the poller takes over.
        await SignPendingTransactionAsync(tester, payer, startResult.PendingTransactionId!, cts.Token).ConfigureAwait(true);

        // The second signature is the receiver's proposal, which the processor parks on a new
        // pending transaction as soon as the proposal comes back through the directory.
        var proposalPendingTransactionId = await WaitForNextPendingTransactionAsync(
            senderSessionStore,
            senderSessionId,
            startResult.PendingTransactionId!,
            cts.Token).ConfigureAwait(true);
        await SignPendingTransactionAsync(tester, payer, proposalPendingTransactionId, cts.Token).ConfigureAwait(true);

        var completedSession = await WaitForTerminalSessionAsync(senderSessionStore, senderSessionId, cts.Token).ConfigureAwait(true);
        Assert.Equal(PayjoinSenderSessionStatus.CompletedPayjoin, completedSession.Status);
        Assert.NotNull(completedSession.BroadcastTransactionId);
        Assert.NotEqual(startResult.OriginalTransactionId, completedSession.BroadcastTransactionId);

        var rewardAddress = await tester.ExplorerNode.GetNewAddressAsync(cts.Token).ConfigureAwait(true);
        await tester.ExplorerNode.GenerateToAddressAsync(1, rewardAddress, cts.Token).ConfigureAwait(true);

        await context.Merchant.WaitInvoicePaid(invoiceId).WaitAsync(cts.Token).ConfigureAwait(true);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyRemovedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);

        var bestBlock = await tester.ExplorerNode.GetBestBlockHashAsync(cts.Token).ConfigureAwait(true);
        var broadcastTransaction = await tester.ExplorerNode
            .GetRawTransactionAsync(uint256.Parse(completedSession.BroadcastTransactionId!), bestBlock, cancellationToken: cts.Token)
            .ConfigureAwait(true);

        Assert.Contains(
            broadcastTransaction.Inputs,
            input => receiverOutpointsBeforePayment.Contains(input.PrevOut.ToString()));
    }

    /// <summary>
    /// Plays the operator at BTCPay's pending-transaction screen: read the transaction, sign it
    /// with the seed the server does not hold, and hand the signature back.
    /// </summary>
    private static async Task SignPendingTransactionAsync(
        ServerTester tester,
        TestAccount payer,
        string pendingTransactionId,
        CancellationToken cancellationToken)
    {
        var pendingTransactionService = tester.PayTester.GetService<PendingTransactionService>();
        var fullId = new PendingTransactionService.PendingTransactionFullId(
            PayjoinConstants.BitcoinCode,
            payer.StoreId,
            pendingTransactionId);
        var pendingTransaction = await pendingTransactionService.GetPendingTransaction(fullId).ConfigureAwait(true);
        Assert.NotNull(pendingTransaction);

        var network = tester.NetworkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode);
        Assert.NotNull(network);
        var blob = pendingTransaction!.GetBlob();
        Assert.NotNull(blob);
        var unsignedPsbt = PSBT.Parse(blob!.PSBT, network!.NBitcoinNetwork);
        var signedPsbt = await payer.Sign(unsignedPsbt).ConfigureAwait(true);

        var collected = await pendingTransactionService
            .CollectSignature(fullId, signedPsbt, cancellationToken)
            .ConfigureAwait(true);
        Assert.NotNull(collected);
        Assert.Equal(PendingTransactionState.Signed, collected!.State);
    }

    private static async Task<string> WaitForNextPendingTransactionAsync(
        PayjoinSenderSessionStore senderSessionStore,
        string senderSessionId,
        string previousPendingTransactionId,
        CancellationToken cancellationToken)
    {
        string? pendingTransactionId = null;
        await AsyncPolling.WaitUntilAsync(
            PayjoinIntegrationTestSupport.TestTimeout,
            TimeSpan.FromSeconds(1),
            _ =>
            {
                if (senderSessionStore.TryGetSession(senderSessionId, out var session) &&
                    session!.Status == PayjoinSenderSessionStatus.AwaitingSignature &&
                    session.PendingTransactionId is not null &&
                    session.PendingTransactionId != previousPendingTransactionId)
                {
                    pendingTransactionId = session.PendingTransactionId;
                    return Task.FromResult(true);
                }

                return Task.FromResult(false);
            },
            shouldRetry: null,
            _ => $"Sender session {senderSessionId} never asked for a signature on the proposal. Last status: {(senderSessionStore.TryGetSession(senderSessionId, out var last) ? last!.Status.ToString() : "missing")}, failure: {last?.FailureMessage}",
            cancellationToken).ConfigureAwait(true);

        Assert.NotNull(pendingTransactionId);
        return pendingTransactionId!;
    }

    private static async Task<PayjoinSenderSessionState> WaitForTerminalSessionAsync(
        PayjoinSenderSessionStore senderSessionStore,
        string senderSessionId,
        CancellationToken cancellationToken)
    {
        PayjoinSenderSessionState? completedSession = null;
        await AsyncPolling.WaitUntilAsync(
            PayjoinIntegrationTestSupport.TestTimeout,
            TimeSpan.FromSeconds(1),
            _ =>
            {
                if (senderSessionStore.TryGetSession(senderSessionId, out var session) &&
                    session!.Status is not PayjoinSenderSessionStatus.Pending
                        and not PayjoinSenderSessionStatus.AwaitingSignature)
                {
                    completedSession = session;
                    return Task.FromResult(true);
                }

                return Task.FromResult(false);
            },
            shouldRetry: null,
            _ => $"Sender session {senderSessionId} did not complete. Last status: {(senderSessionStore.TryGetSession(senderSessionId, out var last) ? last!.Status.ToString() : "missing")}, failure: {last?.FailureMessage}",
            cancellationToken).ConfigureAwait(true);

        Assert.NotNull(completedSession);
        return completedSession!;
    }
}
