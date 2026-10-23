using Microsoft.EntityFrameworkCore;
using BTCPayServer.Client.Models;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Tests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using System.Collections.Concurrent;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public sealed class PayjoinOriginalPaymentAmountIntegrationTests : UnitTestBase
{
    private const decimal InvoicePrice = 0.1m;
    private const decimal PriorPartialPayment = 0.03m;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan OutcomeTimeout = TimeSpan.FromSeconds(40);

    public PayjoinOriginalPaymentAmountIntegrationTests(ITestOutputHelper helper) : base(helper)
    {
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task InvalidOriginalSignatureIsRejectedByCoreBeforeReceiverContribution()
    {
        await RunScenarioAsync("invalid-signature", null, invalidOriginalSignature: true).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task PublishedOriginalIsRecognizedAndSettlesWithoutReceiverContribution()
    {
        await RunScenarioAsync("already-published", null, originalAlreadyPublished: true).ConfigureAwait(true);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Trait("Integration", "Integration")]
    public async Task RejectedOriginalReplayKeepsPreparedSettlementAccounting(bool missingExpectation, bool disableOutputSubstitution)
    {
        await RunScenarioAsync("rejected-prepared-replay", null, disableOutputSubstitution: disableOutputSubstitution,
            rejectedReplayMissingExpectation: missingExpectation).ConfigureAwait(true);
    }

    [Theory]
    [InlineData("close", false)]
    [InlineData("close", true)]
    [InlineData("cleanup", false)]
    [InlineData("cleanup", true)]
    [InlineData("native", false)]
    [InlineData("native", true)]
    [Trait("Integration", "Integration")]
    public async Task PreparedReplayBackfillsBeforeExpiryDuringOriginalCheckAndSettles(string termination, bool disableOutputSubstitution)
    {
        await RunScenarioAsync("prepared-await-" + termination, null, disableOutputSubstitution: disableOutputSubstitution,
            rejectedReplayMissingExpectation: true, preparedCheckTermination: termination).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task UnavailableContributedInputResumesSameAttemptAndCreditsOnlySenderAmount()
    {
        await RunScenarioAsync("contributed-input-recovery", null, unavailableContributedInput: true).ConfigureAwait(true);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("Integration", "Integration")]
    public async Task ExactOriginalPaymentCompletesPayjoinAndCreditsOnlySenderAmount(bool changeDueAfterSessionCreation, bool disableOutputSubstitution)
    {
        await RunScenarioAsync("exact", amountOverride: null, changeDueAfterSessionCreation, disableOutputSubstitution: disableOutputSubstitution).ConfigureAwait(true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Integration", "Integration")]
    public async Task TemporaryLoadFailureAfterSignedProposalPreservesRealSettlementCredit(bool disableOutputSubstitution)
    {
        await RunScenarioAsync("temporary-load-failure", null, disableOutputSubstitution: disableOutputSubstitution,
            loadFailureBeforeBroadcast: true).ConfigureAwait(true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Integration", "Integration")]
    public async Task SignedProposalSurvivesSessionAndReservationExpiryUntilRealSettlementReconciles(bool disableOutputSubstitution)
    {
        await RunScenarioAsync("signed-history-expiry", null, expireBeforeBroadcast: true, disableOutputSubstitution: disableOutputSubstitution).ConfigureAwait(true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Integration", "Integration")]
    public async Task SignedProposalSurvivesNativeExpiryAndReconcilesOnlySenderCredit(bool disableOutputSubstitution)
    {
        await RunScenarioAsync("native-expiry", null, disableOutputSubstitution: disableOutputSubstitution,
            nativeExpiryBeforeBroadcast: true).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task NativeExpiryBeforeCleanupDeadlineDoesNotFailAnUnpreparedBridge()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateScenarioTester("native-expiry-unprepared");
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);
        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);
        var invoice = await PayjoinInvoiceTestHelper.PreparePayjoinInvoiceAsync(tester, context.Merchant, context.Network, cts.Token).ConfigureAwait(true);
        await ReplaceWithShortNativeSessionAsync(tester, context.Merchant.StoreId, invoice, 2, cts.Token).ConfigureAwait(true);
        var sessions = tester.PayTester.GetService<PayjoinReceiverSessionStore>();
        Assert.True(sessions.TryGetSession(invoice.InvoiceId, out var shortSession));
        Assert.NotNull(shortSession);
        await WaitForNativeExpiryAsync(shortSession.GetEvents(), cts.Token).ConfigureAwait(true);
        Assert.True(shortSession.MonitoringExpiresAt > DateTimeOffset.UtcNow);
        Assert.Null(await tester.PayTester.GetService<IPayjoinReceiverSessionGuard>().TryPrepareAsync(shortSession, cts.Token).ConfigureAwait(true));
        Assert.False(sessions.TryGetSession(invoice.InvoiceId, out _));
        var bridge = await tester.PayTester.GetService<IPayjoinAccountingBridgeService>().TryGetByInvoiceIdAsync(invoice.InvoiceId, cts.Token).ConfigureAwait(true);
        Assert.Equal(PayjoinAccountingBridgeStatus.PendingFallback, bridge!.Status);
        Assert.Null(bridge.FailureMessage);
        Assert.Null(bridge.ExpectedFinalTransactionId);
        AssertNoReceiverReservations(tester, invoice.InvoiceId);
        TestLogs.LogInformation($"Native unprepared expiry: invoice={invoice.InvoiceId}; nativeExpired=true; cleanupDeadline={shortSession.MonitoringExpiresAt:O}; bridge={bridge.Status}; failure=null; sessionRemoved=true; reservations=0");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Integration", "Integration")]
    public async Task ExactPaymentWithOneBitcoinReceiverInputCreditsOnlyPointOneBitcoin(bool disableOutputSubstitution)
    {
        await RunScenarioAsync("exact-both-modes", null, disableOutputSubstitution: disableOutputSubstitution).ConfigureAwait(true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Integration", "Integration")]
    public async Task EarlyRejectionThenPublishedPartialAndFreshPayjoinSettleTheSameInvoice(bool disableOutputSubstitution)
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateScenarioTester($"early-retry-{disableOutputSubstitution}");
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);
        var firstPayer = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(tester, context.Network, cancellationToken: cts.Token).ConfigureAwait(true);
        var nextPayer = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(tester, context.Network, cancellationToken: cts.Token).ConfigureAwait(true);
        await context.Merchant.SetNetworkFeeMode(NetworkFeeMode.Never).WaitAsync(cts.Token).ConfigureAwait(true);
        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);
        var invoice = await PayjoinInvoiceTestHelper.PreparePayjoinInvoiceAsync(tester, context.Merchant, context.Network, cts.Token).ConfigureAwait(true);
        var store = tester.PayTester.GetService<PayjoinReceiverSessionStore>();
        Assert.True(store.TryGetSession(invoice.InvoiceId, out var old));
        Assert.NotNull(old);
        var oldPersister = store.CreatePersister(old);
        var prepared = new TaskCompletionSource<Transaction>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var senderCancellation = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var firstSender = new PayjoinTestPayer(tester, firstPayer, context.Network, mineBlockAfterBroadcast: false, disableOutputSubstitution: disableOutputSubstitution);
        var rejected = firstSender.PayAsync(invoice.PaymentUrl, invoice.OhttpRelayUrls, PayjoinIntegrationTestSupport.TestTimeout,
            Money.Satoshis(4_000_000), transaction => prepared.TrySetResult(transaction), senderCancellation.Token);
        await firstSender.OriginalPosted.Task.WaitAsync(cts.Token).ConfigureAwait(true);
        var original = await prepared.Task.WaitAsync(cts.Token).ConfigureAwait(true);
        await AssertEarlyRejectionAsync(tester, invoice.InvoiceId, cts.Token).ConfigureAwait(true);
        AssertSenderPaymentMatches(original, invoice, Money.Satoshis(4_000_000));
        Assert.All(original.Inputs, input => Assert.True(input.WitScript.PushCount > 0));
        Assert.True((await tester.ExplorerNode.TestMempoolAcceptAsync(original, cancellationToken: cts.Token).ConfigureAwait(true)).IsAllowed);
        Assert.Throws<PayjoinSessionStaleException>(() => oldPersister.Load());
        senderCancellation.Cancel();
        Assert.NotNull(await Record.ExceptionAsync(() => rejected).ConfigureAwait(true));
        AssertNoReceiverReservations(tester, invoice.InvoiceId);
        await AssertReceiverCoinsUnspentAsync(tester, invoice.ReceiverOutpointsBeforePayment, cts.Token).ConfigureAwait(true);
        using (var database = tester.PayTester.GetService<PayjoinPluginDbContextFactory>().CreateContext())
        {
            Assert.Empty(database.ReceiverSessions.Where(row => row.InvoiceId == invoice.InvoiceId));
            Assert.Empty(database.ReceiverSessionEvents.Where(row => row.InvoiceId == invoice.InvoiceId));
            Assert.Empty(database.AccountingBridges.Where(row => row.InvoiceId == invoice.InvoiceId));
            foreach (var input in original.Inputs)
                Assert.Contains(database.ReceiverSeenInputs, row => row.TransactionId == input.PrevOut.Hash.ToString() && row.OutputIndex == input.PrevOut.N);
        }
        await tester.ExplorerNode.SendRawTransactionAsync(original, cts.Token).ConfigureAwait(true);
        await tester.ExplorerNode.GenerateAsync(1, cts.Token).ConfigureAwait(true);
        var partial = await WaitForInvoiceAsync(tester, invoice.InvoiceId, entity =>
            entity.GetInvoiceState().Status == InvoiceStatus.New && entity.GetInvoiceState().ExceptionStatus == InvoiceExceptionStatus.PaidPartial &&
            AccountedPayments(entity, invoice.PaymentMethodId).Sum(payment => payment.Value) == 0.04m, cts.Token).ConfigureAwait(true);
        Assert.Equal(0.06m, partial.GetPaymentPrompt(invoice.PaymentMethodId)!.Calculate().Due);
        Assert.False(store.TryGetSession(invoice.InvoiceId, out _)); // No background recreation.
        var checkout = await PayjoinIntegrationTestSupport.GetBip21Async(tester, invoice.InvoiceId, cts.Token).ConfigureAwait(true);
        PayjoinIntegrationTestSupport.AssertPayjoinBip21(checkout);
        Assert.True(store.TryGetSession(invoice.InvoiceId, out var next));
        Assert.NotNull(next);
        Assert.NotEqual(old.SessionInstanceId, next.SessionInstanceId);
        using (var replay = global::Payjoin.PayjoinMethods.ReplayReceiverEventLog(store.CreatePersister(next)))
        using (var history = replay.SessionHistory())
        using (var uri = history.PjUri()) Assert.Equal(6_000_000UL, uri.AmountSats());
        var bridge = await tester.PayTester.GetService<IPayjoinAccountingBridgeService>().TryGetByInvoiceIdAsync(invoice.InvoiceId, cts.Token).ConfigureAwait(true);
        Assert.Equal(6_000_000, bridge!.EffectiveInvoiceValueSats);
        var remainder = invoice with { ExpectedDue = 0.06m, PaymentUrl = new Uri(checkout.Bip21, UriKind.Absolute) };
        var payer = new PayjoinTestPayer(tester, nextPayer, context.Network, mineBlockAfterBroadcast: false, disableOutputSubstitution: disableOutputSubstitution);
        var payment = await payer.PayAsync(remainder.PaymentUrl, remainder.OhttpRelayUrls, cts.Token).ConfigureAwait(true);
        Assert.DoesNotContain(payment.SenderTransaction.Inputs, input => original.Inputs.Any(previous => previous.PrevOut == input.PrevOut));
        var settled = await AssertConfirmedPayjoinAccountingAsync(tester, remainder, payment, 2, cts.Token, disableOutputSubstitution: disableOutputSubstitution).ConfigureAwait(true);
        Assert.Equal(0.1m, AccountedPayments(settled, invoice.PaymentMethodId).Sum(item => item.Value));
        TestLogs.LogInformation($"Early retry evidence: modeDisabled={disableOutputSubstitution}; invoice={invoice.InvoiceId}; oldInstance={old.SessionInstanceId}; newInstance={next.SessionInstanceId}; rejectedHex={original.ToHex()}; rejectedPin=10000000; fallbackCredit=4000000; newPin=6000000; finalCredit=6000000; totalCredit=10000000; settled={payment.TransactionId}; seenInputsPreserved=true; freshInputs=true; reservations=0");
    }

    private static async Task AssertEarlyRejectionAsync(ServerTester tester, string invoiceId, CancellationToken token)
    {
        var store = tester.PayTester.GetService<PayjoinReceiverSessionStore>();
        await AsyncPolling.WaitUntilAsync(OutcomeTimeout, PollInterval, cancellation => Task.FromResult(!store.TryGetSession(invoiceId, out _)),
            shouldRetry: null, _ => "Early attempt was not removed", token).ConfigureAwait(false);
        Assert.Null(await tester.PayTester.GetService<IPayjoinAccountingBridgeService>().TryGetByInvoiceIdAsync(invoiceId, token).ConfigureAwait(false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Integration", "Integration")]
    public async Task UnavailableInputsEndUnsignedAttemptAndNextCheckoutPaysTheCurrentRemainder(bool disableOutputSubstitution)
    {
        await RunUnsignedFailureRetryAsync(disableOutputSubstitution, null).ConfigureAwait(true);
    }

    [Theory]
    [InlineData("InvalidDestination", false)]
    [InlineData("InvalidDestination", true)]
    [InlineData("OutputSubstitution", false)]
    [InlineData("OutputSubstitution", true)]
    [Trait("Integration", "Integration")]
    public async Task OutputFailureEndsUnsignedAttemptAndNextCheckoutPaysTheCurrentRemainder(string outputFailure, bool disableOutputSubstitution)
    {
        await RunUnsignedFailureRetryAsync(disableOutputSubstitution, outputFailure).ConfigureAwait(true);
    }

    private async Task RunUnsignedFailureRetryAsync(bool disableOutputSubstitution, string? outputFailure)
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateScenarioTester($"unsigned-{outputFailure ?? "inputs-unavailable"}-{disableOutputSubstitution}");
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, initialFundingUtxoCount: 1, cancellationToken: cts.Token).ConfigureAwait(true);
        var firstPayer = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(tester, context.Network, cancellationToken: cts.Token).ConfigureAwait(true);
        var nextPayer = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(tester, context.Network, cancellationToken: cts.Token).ConfigureAwait(true);
        await context.Merchant.SetNetworkFeeMode(NetworkFeeMode.Never).WaitAsync(cts.Token).ConfigureAwait(true);
        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);
        var invoice = await PayjoinInvoiceTestHelper.PreparePayjoinInvoiceAsync(tester, context.Merchant, context.Network, cts.Token).ConfigureAwait(true);
        var sessions = tester.PayTester.GetService<PayjoinReceiverSessionStore>();
        Assert.True(sessions.TryGetSession(invoice.InvoiceId, out var old));
        Assert.NotNull(old);
        var fault = new OutputFailurePoller(tester, outputFailure);
        await using (fault.ConfigureAwait(true))
        {
            if (outputFailure is not null) await fault.StartAsync(cts.Token).ConfigureAwait(true);

            PayjoinReceiverSessionState? blocking = null;
            if (outputFailure is null)
            {
                var blockingInvoice = await PayjoinInvoiceTestHelper.PreparePayjoinInvoiceAsync(tester, context.Merchant, context.Network, cts.Token).ConfigureAwait(true);
                Assert.True(sessions.TryGetSession(blockingInvoice.InvoiceId, out blocking));
                Assert.NotNull(blocking);
                var receiverOutpoint = OutPoint.Parse(Assert.Single(invoice.ReceiverOutpointsBeforePayment));
                Assert.True(sessions.TryReserveContributedInput(sessions.CreatePersister(blocking), context.Merchant.StoreId, receiverOutpoint, blocking.MonitoringExpiresAt));
            }
            var prepared = new TaskCompletionSource<Transaction>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var sending = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            var first = new PayjoinTestPayer(tester, firstPayer, context.Network, mineBlockAfterBroadcast: false, disableOutputSubstitution: disableOutputSubstitution);
            var failedPayment = first.PayAsync(invoice.PaymentUrl, invoice.OhttpRelayUrls, null, null, tx => prepared.TrySetResult(tx), sending.Token);
            await first.OriginalPosted.Task.WaitAsync(cts.Token).ConfigureAwait(true);
            var original = await prepared.Task.WaitAsync(cts.Token).ConfigureAwait(true);
            await AssertEarlyRejectionAsync(tester, invoice.InvoiceId, cts.Token).ConfigureAwait(true);
            sending.Cancel();
            Assert.NotNull(await Record.ExceptionAsync(() => failedPayment).ConfigureAwait(true));
            AssertSenderPaymentMatches(original, invoice, Money.Coins(0.1m));
            Assert.True((await tester.ExplorerNode.TestMempoolAcceptAsync(original, cancellationToken: cts.Token).ConfigureAwait(true)).IsAllowed);
            await AssertReceiverCoinsUnspentAsync(tester, invoice.ReceiverOutpointsBeforePayment, cts.Token).ConfigureAwait(true);
            AssertNoReceiverReservations(tester, invoice.InvoiceId);
            using (var db = tester.PayTester.GetService<PayjoinPluginDbContextFactory>().CreateContext())
            {
                foreach (var senderInput in original.Inputs)
                    Assert.Contains(db.ReceiverSeenInputs, row => row.TransactionId == senderInput.PrevOut.Hash.ToString() && row.OutputIndex == senderInput.PrevOut.N);
                Assert.Empty(db.ReceiverSessionEvents.Where(row => row.InvoiceId == invoice.InvoiceId));
            }
            if (blocking is not null) Assert.True(sessions.RemoveSession(blocking.InvoiceId, blocking.SessionInstanceId));
            if (outputFailure is not null)
            {
                Assert.NotNull(fault.Attached);
                Assert.Equal(original.GetHash().ToString(), fault.Attached.FallbackTransactionId);
                Assert.Equal(10_000_000, fault.Attached.FallbackValueSats);
                Assert.Equal(10_000_000, fault.Attached.EffectiveInvoiceValueSats);
                Assert.Null(fault.Attached.ExpectedFinalTransactionId);
                Assert.Null(fault.Attached.SettlementScript);
                Assert.Equal(1, fault.Calls);
                fault.Enabled = false;
            }
            var partialTxid = await tester.ExplorerNode.SendToAddressAsync(invoice.InvoiceScript.GetDestinationAddress(context.Network.NBitcoinNetwork)!, Money.Coins(0.03m), cts.Token).ConfigureAwait(true);
            await tester.ExplorerNode.GenerateAsync(1, cts.Token).ConfigureAwait(true);
            await WaitForInvoiceAsync(tester, invoice.InvoiceId, entity => entity.GetInvoiceState().ExceptionStatus == InvoiceExceptionStatus.PaidPartial &&
                entity.GetPaymentPrompt(invoice.PaymentMethodId)!.Calculate().Due == 0.07m, cts.Token).ConfigureAwait(true);
            Assert.False(sessions.TryGetSession(invoice.InvoiceId, out _));
            var checkout = await PayjoinIntegrationTestSupport.GetBip21Async(tester, invoice.InvoiceId, cts.Token).ConfigureAwait(true);
            PayjoinIntegrationTestSupport.AssertPayjoinBip21(checkout);
            Assert.True(sessions.TryGetSession(invoice.InvoiceId, out var next));
            Assert.NotNull(next);
            Assert.NotEqual(old.SessionInstanceId, next.SessionInstanceId);
            var bridge = await tester.PayTester.GetService<IPayjoinAccountingBridgeService>().TryGetByInvoiceIdAsync(invoice.InvoiceId, cts.Token).ConfigureAwait(true);
            Assert.Equal(7_000_000, bridge!.EffectiveInvoiceValueSats);
            var remainder = invoice with
            {
                ExpectedDue = 0.07m,
                PaymentUrl = new Uri(checkout.Bip21, UriKind.Absolute),
                ReceiverOutpointsBeforePayment = await PayjoinIntegrationTestSupport.GetReceiverOutpointsAsync(tester, context.Merchant.StoreId, true, cts.Token).ConfigureAwait(true)
            };
            var payer = new PayjoinTestPayer(tester, nextPayer, context.Network, mineBlockAfterBroadcast: false, disableOutputSubstitution: disableOutputSubstitution);
            var paying = payer.PayAsync(remainder.PaymentUrl, remainder.OhttpRelayUrls, cts.Token);
            if (outputFailure is null)
            {
                await payer.OriginalPosted.Task.WaitAsync(cts.Token).ConfigureAwait(true);
                await tester.PayTester.GetService<IPayjoinReceiverSessionProcessor>().ProcessTickAsync(cts.Token).ConfigureAwait(true);
            }
            var payment = await paying.ConfigureAwait(true);
            Assert.DoesNotContain(payment.SenderTransaction.Inputs, input => original.Inputs.Any(previous => previous.PrevOut == input.PrevOut));
            await AssertConfirmedPayjoinAccountingAsync(tester, remainder, payment, 2, cts.Token, disableOutputSubstitution: disableOutputSubstitution).ConfigureAwait(true);
            TestLogs.LogInformation($"Unsigned retry evidence: failure={outputFailure ?? "inputs-unavailable"}; invoice={invoice.InvoiceId}; oldInstance={old.SessionInstanceId}; newInstance={next.SessionInstanceId}; originalHex={original.ToHex()}; partialTxid={partialTxid}; oldPin=10000000; newPin=7000000; credit=7000000; total=10000000; seenInputsPreserved=true; reservations=0; modeDisabled={disableOutputSubstitution}");
        }
    }

    [Theory]
    [Trait("Integration", "Integration")]
    [InlineData("underpaid", -6_000_000L, false)]
    [InlineData("underpaid", -6_000_000L, true)]
    [InlineData("overpaid", 1L, false)]
    public async Task MismatchedOriginalPaymentRejectsReceiverContributionAndFullInvoiceCredit(string name, long deltaSats, bool disableOutputSubstitution)
    {
        await RunScenarioAsync(name, Money.Coins(InvoicePrice) + Money.Satoshis(deltaSats), disableOutputSubstitution: disableOutputSubstitution).ConfigureAwait(true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Integration", "Integration")]
    public async Task PartialPaymentBeforeSessionCreationPinsAndCreditsOnlyTheRealRemainder(bool disableOutputSubstitution)
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateScenarioTester("partial-before-session");
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);
        var payer = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(tester, context.Network, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.DisablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);
        await context.Merchant.SetNetworkFeeMode(NetworkFeeMode.Never).WaitAsync(cts.Token).ConfigureAwait(true);
        var invoice = await context.Merchant.BitPay.CreateInvoiceAsync(new NBitpayClient.Invoice
        {
            Price = InvoicePrice,
            Currency = PayjoinConstants.BitcoinCode
        }).WaitAsync(cts.Token).ConfigureAwait(true);
        var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(PayjoinConstants.BitcoinCode);
        var invoiceRepository = tester.PayTester.GetService<InvoiceRepository>();
        var unpaid = await invoiceRepository.GetInvoice(invoice.Id).WaitAsync(cts.Token).ConfigureAwait(true);
        Assert.NotNull(unpaid);
        var prompt = unpaid.GetPaymentPrompt(paymentMethodId);
        Assert.NotNull(prompt);
        var invoiceScript = BitcoinAddress.Create(prompt.Destination, context.Network.NBitcoinNetwork).ScriptPubKey;
        Assert.False(tester.PayTester.GetService<PayjoinReceiverSessionStore>().TryGetSession(invoice.Id, out _));

        var partialTransactionId = await tester.ExplorerNode.SendToAddressAsync(
            BitcoinAddress.Create(prompt.Destination, context.Network.NBitcoinNetwork),
            Money.Coins(PriorPartialPayment), cts.Token).ConfigureAwait(true);
        await tester.ExplorerNode.GenerateAsync(1, cts.Token).ConfigureAwait(true);
        var partialInvoice = await WaitForInvoiceAsync(tester, invoice.Id, entity =>
        {
            var state = entity.GetInvoiceState();
            return state.Status == InvoiceStatus.New && state.ExceptionStatus == InvoiceExceptionStatus.PaidPartial &&
                   AccountedPayments(entity, paymentMethodId).Sum(payment => payment.Value) == PriorPartialPayment;
        }, cts.Token).ConfigureAwait(true);
        var remainingDue = partialInvoice.GetPaymentPrompt(paymentMethodId)!.Calculate().Due;
        Assert.Equal(InvoicePrice - PriorPartialPayment, remainingDue);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);
        var bip21 = await PayjoinIntegrationTestSupport.GetBip21Async(tester, invoice.Id, cts.Token).ConfigureAwait(true);
        PayjoinIntegrationTestSupport.AssertPayjoinBip21(bip21);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(tester, invoice.Id, cts.Token).ConfigureAwait(true);
        var bridge = await tester.PayTester.GetService<IPayjoinAccountingBridgeService>()
            .TryGetByInvoiceIdAsync(invoice.Id, cts.Token).ConfigureAwait(true);
        Assert.NotNull(bridge);
        Assert.Equal(Money.Coins(remainingDue).Satoshi, bridge.EffectiveInvoiceValueSats);

        var settings = await PayjoinIntegrationTestSupport.ReadStoreSettingsAsync(tester, context.Merchant.StoreId, cts.Token).ConfigureAwait(true);
        var paymentContext = new PayjoinInvoiceTestHelper.PayjoinInvoiceContext(
            invoice.Id, paymentMethodId, remainingDue,
            await PayjoinIntegrationTestSupport.GetReceiverOutpointsAsync(tester, context.Merchant.StoreId, confirmedOnly: true, cts.Token).ConfigureAwait(true),
            new Uri(bip21.Bip21, UriKind.Absolute), settings.GetEffectiveDirectoryUrls(), settings.GetEffectiveOhttpRelayUrls(), invoiceScript);
        var payjoinPayer = new PayjoinTestPayer(tester, payer, context.Network, mineBlockAfterBroadcast: false, disableOutputSubstitution: disableOutputSubstitution);
        var payment = await payjoinPayer.PayAsync(paymentContext.PaymentUrl, paymentContext.OhttpRelayUrls, cts.Token).ConfigureAwait(true);
        var settled = await AssertConfirmedPayjoinAccountingAsync(tester, paymentContext, payment, expectedPaymentCount: 2, cts.Token, disableOutputSubstitution: disableOutputSubstitution).ConfigureAwait(true);
        var handler = tester.PayTester.GetService<PaymentMethodHandlerDictionary>().GetBitcoinHandler(PayjoinConstants.BitcoinCode);
        var priorPayment = Assert.Single(AccountedPayments(settled, paymentMethodId),
            item => item.GetDetails<BitcoinLikePaymentData>(handler).Outpoint.Hash == partialTransactionId);
        Assert.Equal(PriorPartialPayment, priorPayment.Value);
        TestLogs.LogInformation($"Partial payment {partialTransactionId}: {PriorPartialPayment} BTC/{priorPayment.Status}; session pin and final Payjoin credit: {remainingDue} BTC; total invoice credit: {InvoicePrice} BTC.");
    }

    private ServerTester CreateScenarioTester(string name)
    {
        var tester = CreateServerTester(scope: $"original-amount-{name}-{Guid.NewGuid():N}", newDb: true);
        tester.DeleteStore = false;
        return tester;
    }

    private async Task RunScenarioAsync(string name, Money? amountOverride, bool changeDueAfterSessionCreation = false, bool expireBeforeBroadcast = false, bool disableOutputSubstitution = false,
        bool nativeExpiryBeforeBroadcast = false, bool loadFailureBeforeBroadcast = false, bool unavailableContributedInput = false,
        bool invalidOriginalSignature = false, bool originalAlreadyPublished = false, bool? rejectedReplayMissingExpectation = null,
        string? preparedCheckTermination = null)
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateScenarioTester(name);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);
        var payer = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(tester, context.Network, cancellationToken: cts.Token).ConfigureAwait(true);
        using var rejectionLog = new ReceiverAmountRejectionLog();
        tester.PayTester.GetService<ILoggerFactory>().AddProvider(rejectionLog);

        await context.Merchant.SetNetworkFeeMode(NetworkFeeMode.Never).WaitAsync(cts.Token).ConfigureAwait(true);
        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);
        var paymentContext = await PayjoinInvoiceTestHelper.PreparePayjoinInvoiceAsync(
            tester, context.Merchant, context.Network, cts.Token).ConfigureAwait(true);
        if (nativeExpiryBeforeBroadcast || preparedCheckTermination == "native")
            paymentContext = await ReplaceWithShortNativeSessionAsync(tester, context.Merchant.StoreId, paymentContext, 60, cts.Token).ConfigureAwait(true);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(tester, paymentContext.InvoiceId, cts.Token).ConfigureAwait(true);
        Assert.NotEmpty(paymentContext.ReceiverOutpointsBeforePayment);
        var inputOutage = unavailableContributedInput ? new ContributedInputOutage(tester) : null;
        var controlledPoller = new OutputFailurePoller(tester, null) { Enabled = false };
        await using var controlledPollerScope = controlledPoller.ConfigureAwait(false);
        if (inputOutage is not null) await controlledPoller.StartAsync(cts.Token, inputOutage, inputOutage).ConfigureAwait(true);
        var broadcastChecks = new RecordingBroadcastValidator(tester.PayTester.GetService<IPayjoinOriginalTransactionValidator>());
        var receiverActions = invalidOriginalSignature || originalAlreadyPublished ? new ContributedInputOutage(tester) : null;
        if (receiverActions is not null)
            await controlledPoller.StartAsync(cts.Token, receiverActions, receiverActions, broadcastChecks).ConfigureAwait(true);
        if (changeDueAfterSessionCreation)
        {
            var partialTx = await tester.ExplorerNode.SendToAddressAsync(
                paymentContext.InvoiceScript.GetDestinationAddress(context.Network.NBitcoinNetwork)!,
                Money.Coins(PriorPartialPayment), cts.Token).ConfigureAwait(true);
            await tester.ExplorerNode.GenerateAsync(1, cts.Token).ConfigureAwait(true);
            var partial = await WaitForInvoiceAsync(tester, paymentContext.InvoiceId,
                entity => AccountedPayments(entity, paymentContext.PaymentMethodId).Sum(p => p.Value) == PriorPartialPayment,
                cts.Token).ConfigureAwait(true);
            Assert.Equal(InvoicePrice - PriorPartialPayment, partial.GetPaymentPrompt(paymentContext.PaymentMethodId)!.Calculate().Due);
            var sessions = tester.PayTester.GetService<PayjoinReceiverSessionStore>();
            Assert.True(sessions.TryGetSession(paymentContext.InvoiceId, out var session));
            using var replay = global::Payjoin.PayjoinMethods.ReplayReceiverEventLog(sessions.CreatePersister(session!));
            using var history = replay.SessionHistory();
            using var uri = history.PjUri();
            Assert.Equal(10_000_000UL, uri.AmountSats());
            TestLogs.LogInformation($"Live due changed after session creation: prior payment={partialTx}, due=7000000 sats, session pin=10000000 sats; retained original URI.");
        }
        var originalPrepared = new TaskCompletionSource<Transaction>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var senderCancellation = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var payjoinPayer = new PayjoinTestPayer(tester, payer, context.Network, mineBlockAfterBroadcast: false, disableOutputSubstitution: disableOutputSubstitution);
        if (invalidOriginalSignature || originalAlreadyPublished)
        {
            payjoinPayer.BeforeOriginalPostAsync = async (psbt, token) =>
            {
                if (invalidOriginalSignature)
                {
                    var pushes = psbt.Inputs[0].FinalScriptWitness!.Pushes.Select(push => push.ToArray()).ToArray();
                    pushes[0][10] ^= 1;
                    psbt.Inputs[0].FinalScriptWitness = new WitScript(pushes);
                }
                else await tester.ExplorerNode.SendRawTransactionAsync(psbt.ExtractTransaction(), cancellationToken: token).ConfigureAwait(false);
            };
        }
        if (expireBeforeBroadcast || nativeExpiryBeforeBroadcast || loadFailureBeforeBroadcast || rejectedReplayMissingExpectation.HasValue)
        {
            payjoinPayer.BeforeBroadcastAsync = async (finalTransaction, token) =>
            {
                var sessions = tester.PayTester.GetService<PayjoinReceiverSessionStore>();
                await AsyncPolling.WaitUntilAsync(OutcomeTimeout, PollInterval, _ =>
                {
                    if (!sessions.TryGetSession(paymentContext.InvoiceId, out var current)) return Task.FromResult(false);
                    using var replay = global::Payjoin.PayjoinMethods.ReplayReceiverEventLog(PayjoinReceiverSessionStore.Snapshot(current!));
                    using var state = replay.State();
                    return Task.FromResult(state is global::Payjoin.ReceiveSession.Monitor);
                }, shouldRetry: null, _ => "Receiver has not persisted its posted proposal", token).ConfigureAwait(false);
                Assert.True(sessions.TryGetSession(paymentContext.InvoiceId, out var held));
                Assert.NotNull(held);
                var events = held.GetEvents();
                var uri = held.PayjoinUri;
                var bridge = await tester.PayTester.GetService<IPayjoinAccountingBridgeService>().TryGetByInvoiceIdAsync(paymentContext.InvoiceId, token).ConfigureAwait(false);
                Assert.NotNull(bridge);
                Assert.Equal(PayjoinAccountingBridgeStatus.PendingFinalTransaction, bridge.Status);
                Assert.Equal(finalTransaction.GetHash().ToString(), bridge.ExpectedFinalTransactionId);
                Assert.True((await tester.ExplorerNode.TestMempoolAcceptAsync(finalTransaction, cancellationToken: token).ConfigureAwait(false)).IsAllowed);
                var recovered = RecoverSignedProposal(events, context.Network.NBitcoinNetwork);
                if (rejectedReplayMissingExpectation.HasValue)
                {
                    await controlledPoller.StartAsync(token, validator: broadcastChecks).ConfigureAwait(false);
                    await controlledPoller.DisposeAsync().ConfigureAwait(false);
                    var prefix = Enumerable.Range(1, events.Length).First(count =>
                    {
                        using var replay = global::Payjoin.PayjoinMethods.ReplayReceiverEventLog(new PrefixPersister(events[..count]));
                        using var state = replay.State();
                        return state is global::Payjoin.ReceiveSession.PayjoinProposal;
                    });
                    using (var db = tester.PayTester.GetService<PayjoinPluginDbContextFactory>().CreateContext())
                    {
                        db.ReceiverSessionEvents.RemoveRange(db.ReceiverSessionEvents.Where(e => e.InvoiceId == held.InvoiceId && e.Sequence > prefix));
                        var row = db.ReceiverSessions.Single(s => s.InvoiceId == held.InvoiceId);
                        row.AttemptVersion++;
                        if (rejectedReplayMissingExpectation.Value)
                        {
                            var data = db.AccountingBridges.Single(b => b.InvoiceId == held.InvoiceId);
                            data.ExpectedFinalTransactionId = null;
                            data.ExpectedFinalOutputIndex = null;
                            data.ExpectedFinalValueSats = null;
                        }
                        await db.SaveChangesAsync(token).ConfigureAwait(false);
                    }
                    broadcastChecks.Reject = preparedCheckTermination != "native";
                    if (preparedCheckTermination is not null)
                    {
                        broadcastChecks.BeforeResult = async () =>
                        {
                            var armed = await tester.PayTester.GetService<IPayjoinAccountingBridgeService>().TryGetByInvoiceIdAsync(held.InvoiceId, token).ConfigureAwait(false);
                            Assert.Equal(bridge.ExpectedFinalTransactionId, armed!.ExpectedFinalTransactionId);
                            Assert.Equal(bridge.ExpectedFinalOutputIndex, armed.ExpectedFinalOutputIndex);
                            Assert.Equal(bridge.ExpectedFinalValueSats, armed.ExpectedFinalValueSats);
                            if (preparedCheckTermination == "close")
                                sessions.RequestClose(held.InvoiceId, InvoiceStatus.Expired, held.SessionInstanceId);
                            else if (preparedCheckTermination == "cleanup")
                            {
                                using var db = tester.PayTester.GetService<PayjoinPluginDbContextFactory>().CreateContext();
                                db.ReceiverSessions.Single(s => s.InvoiceId == held.InvoiceId).MonitoringExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
                                await db.SaveChangesAsync(token).ConfigureAwait(false);
                            }
                            else await WaitForNativeExpiryAsync(events[..prefix], token).ConfigureAwait(false);
                        };
                    }
                    await controlledPoller.StartAsync(token, validator: broadcastChecks).ConfigureAwait(false);
                    await AsyncPolling.WaitUntilAsync(preparedCheckTermination == "native" ? TimeSpan.FromSeconds(90) : OutcomeTimeout, PollInterval, async _ =>
                    {
                        var current = await tester.PayTester.GetService<IPayjoinAccountingBridgeService>().TryGetByInvoiceIdAsync(held.InvoiceId, token).ConfigureAwait(false);
                        var verdict = broadcastChecks.Reject ? OriginalTransactionVerdict.Rejected : OriginalTransactionVerdict.Accepted;
                        return broadcastChecks.Results.Any(r => r.Verdict == verdict) && current?.ExpectedFinalTransactionId == finalTransaction.GetHash().ToString();
                    }, null, _ => "Prepared expectation was not retained/backfilled", token).ConfigureAwait(false);
                    var replayHeld = sessions.GetAttempt(held.InvoiceId, held.SessionInstanceId);
                    Assert.Equal(events[..prefix], replayHeld.GetEvents());
                    Assert.Equal(uri, replayHeld.PayjoinUri);
                    var currentBridge = await tester.PayTester.GetService<IPayjoinAccountingBridgeService>().TryGetByInvoiceIdAsync(held.InvoiceId, token).ConfigureAwait(false);
                    Assert.Null(currentBridge!.FailureMessage);
                    Assert.Equal(PayjoinAccountingBridgeStatus.PendingFinalTransaction, currentBridge.Status);
                    Assert.Equal(bridge.ExpectedFinalOutputIndex, currentBridge.ExpectedFinalOutputIndex);
                    Assert.Equal(bridge.ExpectedFinalValueSats, currentBridge.ExpectedFinalValueSats);
                    Assert.Contains(await tester.PayTester.GetService<IPayjoinAccountingBridgeService>().GetPendingReconciliationAsync(DateTimeOffset.UtcNow, token).ConfigureAwait(false), b => b.InvoiceId == held.InvoiceId);
                    TestLogs.LogInformation($"Prepared check termination={preparedCheckTermination ?? "none"}; URI and all {prefix} events retained; expected={currentBridge.ExpectedFinalTransactionId}:{currentBridge.ExpectedFinalOutputIndex}, value={currentBridge.ExpectedFinalValueSats}; reconciliation eligible");
                    TestLogs.LogInformation($"Rejected original replay: invoice={held.InvoiceId}; backfill={rejectedReplayMissingExpectation.Value}; expected={currentBridge.ExpectedFinalTransactionId}; events={prefix}; noRepost=true; failure=null; reconciliation=pending");
                    return;
                }
                if (loadFailureBeforeBroadcast)
                {
                    string connectionString;
                    using (var db = tester.PayTester.GetService<PayjoinPluginDbContextFactory>().CreateContext())
                        connectionString = db.Database.GetDbConnection().ConnectionString;
                    var faulty = new FailingLoadFactory(connectionString);
                    var failedStore = new PayjoinReceiverSessionStore(faulty, tester.PayTester.GetService<IPayjoinUniqueConstraintViolationDetector>());
                    var guard = new PayjoinReceiverSessionGuard(failedStore, tester.PayTester.GetService<BTCPayNetworkProvider>(),
                        tester.PayTester.GetService<InvoiceRepository>(), NullLogger<PayjoinReceiverSessionGuard>.Instance);
                    Assert.Null(await guard.TryPrepareAsync(held, token).ConfigureAwait(false));
                    Assert.Equal(1, faulty.Failures);
                    Assert.Equal(bridge, await tester.PayTester.GetService<IPayjoinAccountingBridgeService>().TryGetByInvoiceIdAsync(paymentContext.InvoiceId, token).ConfigureAwait(false));
                    Assert.Contains(await tester.PayTester.GetService<IPayjoinAccountingBridgeService>().GetPendingReconciliationAsync(DateTimeOffset.UtcNow, token).ConfigureAwait(false), b => b.InvoiceId == paymentContext.InvoiceId);
                    Assert.True(sessions.TryGetSession(paymentContext.InvoiceId, out var preserved));
                    Assert.Equal(events, preserved!.GetEvents());
                    Assert.Equal(uri, preserved.PayjoinUri);
                    using var recoveredGuard = await guard.TryPrepareAsync(preserved, token).ConfigureAwait(false);
                    Assert.NotNull(recoveredGuard);
                    TestLogs.LogInformation($"Temporary load failure: invoice={paymentContext.InvoiceId}; failures=1; expected={bridge.ExpectedFinalTransactionId}; status={bridge.Status}; events={events.Length}; history unchanged; reconciliation eligible");
                    return;
                }
                if (nativeExpiryBeforeBroadcast)
                {
                    await WaitForNativeExpiryAsync(events, token).ConfigureAwait(false);
                    Assert.True(held.MonitoringExpiresAt > DateTimeOffset.UtcNow);
                    TestLogs.LogInformation($"Native signed expiry: invoice={paymentContext.InvoiceId}; nativeExpired=true; cleanupDeadline={held.MonitoringExpiresAt:O}; expectation={bridge.ExpectedFinalTransactionId}; modeDisabled={disableOutputSubstitution}");
                }
                else
                {
                    using var database = tester.PayTester.GetService<PayjoinPluginDbContextFactory>().CreateContext();
                    database.ReceiverSessions.Single(row => row.InvoiceId == paymentContext.InvoiceId).MonitoringExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
                    await database.SaveChangesAsync(token).ConfigureAwait(false);
                }
                Assert.Equal(1, sessions.CleanupExpiredInputReservations(DateTimeOffset.UtcNow.AddDays(2)));
                Assert.True(sessions.TryGetSession(paymentContext.InvoiceId, out var expired));
                Assert.NotNull(expired);
                Assert.False(expired.TryGetContributedInput(out _));
                Assert.Null(await tester.PayTester.GetService<IPayjoinReceiverSessionGuard>().TryPrepareAsync(expired, token).ConfigureAwait(false));
                Assert.False(sessions.RemoveSession(paymentContext.InvoiceId));
                Assert.False(sessions.TryRemoveSessionUnlessNegotiating(paymentContext.InvoiceId));
                Assert.True(sessions.TryGetSession(paymentContext.InvoiceId, out var retained));
                Assert.NotNull(retained);
                Assert.Equal(uri, retained.PayjoinUri);
                Assert.Equal(events, retained.GetEvents());
                Assert.Equal(bridge, await tester.PayTester.GetService<IPayjoinAccountingBridgeService>().TryGetByInvoiceIdAsync(paymentContext.InvoiceId, token).ConfigureAwait(false));
                Assert.Equal(finalTransaction.GetHash(), recovered.GetGlobalTransaction().GetHash());
                var receiverInput = Assert.Single(recovered.Inputs, input => paymentContext.ReceiverOutpointsBeforePayment.Contains(input.PrevOut.ToString()));
                Assert.NotNull(receiverInput.FinalScriptWitness);
                Assert.True(receiverInput.FinalScriptWitness.PushCount > 0);
                var finalized = retained.GetEvents().Select(Newtonsoft.Json.Linq.JObject.Parse).Single(item => item["FinalizedProposal"] is not null)["FinalizedProposal"]!;
                Assert.Contains(finalized["inputs"]!, input => input["final_script_witness"] is Newtonsoft.Json.Linq.JArray witness &&
                    witness.Values<string>().SequenceEqual(receiverInput.FinalScriptWitness.Pushes.Select(Convert.ToHexString), StringComparer.OrdinalIgnoreCase));
                Assert.NotNull(await tester.ExplorerNode.GetTxOutAsync(receiverInput.PrevOut.Hash, checked((int)receiverInput.PrevOut.N), true, token).ConfigureAwait(false));
                AssertNoReceiverReservations(tester, paymentContext.InvoiceId);
                var bridges = tester.PayTester.GetService<IPayjoinAccountingBridgeService>();
                var expiredBridges = await bridges.ExpirePendingAsync(bridge.ExpiresAt!.Value + PayjoinAccountingBridgeService.ArmedBridgeGracePeriod + TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                Assert.Equal(PayjoinAccountingBridgeStatus.Expired, Assert.Single(expiredBridges, item => item.InvoiceId == paymentContext.InvoiceId).Status);
                Assert.DoesNotContain(sessions.GetSessions(), item => item.InvoiceId == paymentContext.InvoiceId);
                Assert.DoesNotContain(await bridges.GetPendingReconciliationAsync(DateTimeOffset.UtcNow, token).ConfigureAwait(false), item => item.InvoiceId == paymentContext.InvoiceId);
                var diagnostic = sessions.GetAttempt(paymentContext.InvoiceId, retained.SessionInstanceId);
                Assert.Equal(uri, diagnostic.PayjoinUri);
                Assert.Equal(events, diagnostic.GetEvents());
                Assert.True(Assert.Single((await bridges.GetRequiringAttentionAsync(context.Merchant.StoreId, token).ConfigureAwait(false)).Bridges,
                    item => item.InvoiceId == paymentContext.InvoiceId).RetryAllowed);
                var retried = await bridges.TryRetryAsync(paymentContext.InvoiceId, context.Merchant.StoreId, DateTimeOffset.UtcNow, token).ConfigureAwait(false);
                Assert.NotNull(retried);
                Assert.Equal(PayjoinAccountingBridgeStatus.PendingFinalTransaction, retried.Status);
                Assert.Equal(finalTransaction.GetHash().ToString(), retried.ExpectedFinalTransactionId);
                Assert.Contains(await bridges.GetPendingReconciliationAsync(DateTimeOffset.UtcNow, token).ConfigureAwait(false), item => item.InvoiceId == paymentContext.InvoiceId);
                Assert.Null(await tester.PayTester.GetService<IPayjoinReceiverSessionGuard>().TryPrepareAsync(
                    sessions.GetAttempt(paymentContext.InvoiceId, retained.SessionInstanceId), token).ConfigureAwait(false));
                Assert.Equal(events, sessions.GetAttempt(paymentContext.InvoiceId, retained.SessionInstanceId).GetEvents());
                Assert.Equal(uri, sessions.GetAttempt(paymentContext.InvoiceId, retained.SessionInstanceId).PayjoinUri);
                TestLogs.LogInformation($"Expired armed retry: invoice={paymentContext.InvoiceId}; state=Expired->Retry; expected={retried.ExpectedFinalTransactionId}; nativeNegotiationStopped=true; historyUnchanged=true; reservations=0; modeDisabled={disableOutputSubstitution}");
                TestLogs.LogInformation($"Signed history expiry evidence: invoice={paymentContext.InvoiceId}; expected={bridge.ExpectedFinalTransactionId}; receiver={receiverInput.PrevOut}; witness={receiverInput.FinalScriptWitness}; events={events.Length}; reservations=0; uri={uri}; proposal={recovered.ToBase64()}");
                TestLogs.LogInformation($"Signed retained event log: {Newtonsoft.Json.JsonConvert.SerializeObject(events)}");
            };
        }
        using var receiverPassCancellation = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var receiverPass = nativeExpiryBeforeBroadcast
            ? tester.PayTester.GetService<IPayjoinReceiverSessionProcessor>().ProcessTickAsync(receiverPassCancellation.Token)
            : Task.CompletedTask;
        var payTask = payjoinPayer.PayAsync(
            paymentContext.PaymentUrl, paymentContext.OhttpRelayUrls, preProposalPollDelay: null,
            amountOverride, transaction => originalPrepared.TrySetResult(transaction), senderCancellation.Token);
        try
        {
            await Task.WhenAny(originalPrepared.Task, payTask).WaitAsync(cts.Token).ConfigureAwait(true);
            if (!originalPrepared.Task.IsCompleted)
            {
                await payTask.ConfigureAwait(true);
            }

            var original = await originalPrepared.Task.WaitAsync(cts.Token).ConfigureAwait(true);
            TestLogs.LogInformation($"Evidence {name}: invoice {paymentContext.InvoiceId}, original hex {original.ToHex()}");
            AssertSenderPaymentMatches(original, paymentContext, amountOverride ?? Money.Coins(paymentContext.ExpectedDue));
            Assert.DoesNotContain(original.Inputs, input => paymentContext.ReceiverOutpointsBeforePayment.Contains(input.PrevOut.ToString()));
            if (invalidOriginalSignature || originalAlreadyPublished)
            {
                if (invalidOriginalSignature)
                {
                    var coreVerdict = await tester.ExplorerNode.TestMempoolAcceptAsync(original, cancellationToken: cts.Token).ConfigureAwait(true);
                    Assert.False(coreVerdict.IsAllowed);
                    await AsyncPolling.WaitUntilAsync(OutcomeTimeout, PollInterval,
                        _ => Task.FromResult(broadcastChecks.Results.Any(r => r.Verdict == OriginalTransactionVerdict.Rejected)), null,
                        _ => "NBXplorer did not return an explicit signature rejection", cts.Token).ConfigureAwait(true);
                    var observed = Assert.Single(broadcastChecks.Results, r => r.Verdict == OriginalTransactionVerdict.Rejected);
                    Assert.NotEmpty(observed.Reason);
                    await AsyncPolling.WaitUntilAsync(OutcomeTimeout, PollInterval, _ =>
                    {
                        var sessions = tester.PayTester.GetService<PayjoinReceiverSessionStore>();
                        if (!sessions.TryGetSession(paymentContext.InvoiceId, out var saved)) return Task.FromResult(true);
                        using var replay = global::Payjoin.PayjoinMethods.ReplayReceiverEventLog(PayjoinReceiverSessionStore.Snapshot(saved!));
                        using var state = replay.State();
                        return Task.FromResult(state is global::Payjoin.ReceiveSession.HasReplyableError or global::Payjoin.ReceiveSession.ReceiverPendingFallback or global::Payjoin.ReceiveSession.Closed);
                    }, null, _ => "Invalid original did not persist its native rejection", cts.Token).ConfigureAwait(true);
                    var rejectionBridge = await tester.PayTester.GetService<IPayjoinAccountingBridgeService>().TryGetByInvoiceIdAsync(paymentContext.InvoiceId, cts.Token).ConfigureAwait(true);
                    Assert.Null(rejectionBridge!.ExpectedFinalTransactionId);
                    Assert.Null(rejectionBridge.FailureMessage);
                    Assert.False(payTask.IsCompletedSuccessfully);
                    var signatureUnpaidInvoice = await tester.PayTester.GetService<InvoiceRepository>().GetInvoice(paymentContext.InvoiceId).ConfigureAwait(true);
                    Assert.Empty(AccountedPayments(signatureUnpaidInvoice, paymentContext.PaymentMethodId));
                    Assert.Equal(InvoiceStatus.New, signatureUnpaidInvoice.GetInvoiceState().Status);
                    TestLogs.LogInformation($"Core original rejection: invoice={paymentContext.InvoiceId}; original={original.GetHash()}; reason={observed.Reason}; contributions=0; signing=0; proposalPosts=0; credit=0");
                }
                else
                {
                    OriginalTransactionValidation? known = null;
                    await AsyncPolling.WaitUntilAsync(OutcomeTimeout, PollInterval, async _ =>
                    {
                        known = await tester.PayTester.GetService<IPayjoinOriginalTransactionValidator>().ValidateAsync(original.ToBytes(), cts.Token).ConfigureAwait(false);
                        return known.Verdict == OriginalTransactionVerdict.AlreadyKnown;
                    }, null, _ => $"Exact published original not observed: {known}", cts.Token).ConfigureAwait(true);
                    await tester.ExplorerNode.GenerateAsync(1, cts.Token).ConfigureAwait(true);
                    var settled = await WaitForInvoiceAsync(tester, paymentContext.InvoiceId, invoice => invoice.GetInvoiceState().Status == InvoiceStatus.Settled, cts.Token).ConfigureAwait(true);
                    var credit = Assert.Single(AccountedPayments(settled, paymentContext.PaymentMethodId));
                    Assert.Equal(InvoicePrice, credit.Value);
                    TestLogs.LogInformation($"Known original settled: invoice={paymentContext.InvoiceId}; original={original.GetHash()}; credit={credit.Value}; state=Settled; contributions=0; signing=0; proposalPosts=0");
                }
                Assert.Equal(0, receiverActions!.Contributions);
                Assert.Equal(0, receiverActions.Finalizations);
                Assert.Equal(0, receiverActions.Posts);
                Assert.Equal(0, controlledPoller.TotalCalls);
                AssertNoReceiverReservations(tester, paymentContext.InvoiceId);
                foreach (var outpoint in paymentContext.ReceiverOutpointsBeforePayment.Select(OutPoint.Parse))
                    Assert.NotNull(await tester.ExplorerNode.GetTxOutAsync(outpoint.Hash, checked((int)outpoint.N), true, cts.Token).ConfigureAwait(true));
                return;
            }
            if (amountOverride is null)
            {
                if (inputOutage is not null)
                {
                    await AsyncPolling.WaitUntilAsync(OutcomeTimeout, PollInterval, _ => Task.FromResult(inputOutage.Lookups.Count >= 2),
                        shouldRetry: null, _ => "Missing contributed coin was not retried", cts.Token).ConfigureAwait(true);
                    var sessions = tester.PayTester.GetService<PayjoinReceiverSessionStore>();
                    Assert.True(sessions.TryGetSession(paymentContext.InvoiceId, out var held));
                    Assert.NotNull(held);
                    Assert.True(held.TryGetContributedInput(out var outpoint));
                    var bridges = tester.PayTester.GetService<IPayjoinAccountingBridgeService>();
                    var pausedBridge = await bridges.TryGetByInvoiceIdAsync(held.InvoiceId, cts.Token).ConfigureAwait(true);
                    Assert.Null(pausedBridge!.ExpectedFinalTransactionId);
                    Assert.Null(pausedBridge.FailureMessage);
                    Assert.Equal(0, inputOutage.Finalizations);
                    Assert.Equal(1, inputOutage.Contributions);
                    Assert.False(payTask.IsCompleted);
                    await controlledPoller!.DisposeAsync().ConfigureAwait(true);
                    await controlledPoller.StartAsync(cts.Token, inputOutage, inputOutage).ConfigureAwait(true);
                    var previousReads = inputOutage.Lookups.Count;
                    await AsyncPolling.WaitUntilAsync(OutcomeTimeout, PollInterval, _ => Task.FromResult(inputOutage.Lookups.Count > previousReads),
                        shouldRetry: null, _ => "Recreated processor did not recheck the contributed coin", cts.Token).ConfigureAwait(true);
                    var retained = sessions.GetAttempt(held.InvoiceId, held.SessionInstanceId);
                    Assert.Equal(held.PayjoinUri, retained.PayjoinUri);
                    Assert.Equal(held.GetEvents(), retained.GetEvents());
                    Assert.Equal(pausedBridge, await bridges.TryGetByInvoiceIdAsync(held.InvoiceId, cts.Token).ConfigureAwait(true));
                    Assert.All(inputOutage.Lookups, value => Assert.Equal(outpoint!.ToString(), value));
                    TestLogs.LogInformation($"Contributed input recovery: invoice={held.InvoiceId}; instance={held.SessionInstanceId}; outpoint={outpoint}; unavailableReads={inputOutage.Lookups.Count}; URI/events/pausedBridge unchanged after processor restart; finalizations=0; contributions=1");
                    inputOutage.Available = true;
                }
                var payment = await payTask.ConfigureAwait(true);
                if (inputOutage is not null)
                {
                    Assert.Equal(1, inputOutage.Contributions);
                    Assert.Equal(1, inputOutage.Finalizations);
                    Assert.Contains(payment.ProposalTransaction.Inputs, input => input.PrevOut.ToString() == inputOutage.Lookups.First());
                }
                await AssertConfirmedPayjoinAccountingAsync(tester, paymentContext, payment,
                    expectedPaymentCount: changeDueAfterSessionCreation ? 2 : 1, cts.Token,
                    expectedTotal: changeDueAfterSessionCreation ? InvoicePrice + PriorPartialPayment : InvoicePrice,
                    disableOutputSubstitution: disableOutputSubstitution).ConfigureAwait(true);
                return;
            }

            await AsyncPolling.WaitUntilAsync(OutcomeTimeout, PollInterval,
                _ => Task.FromResult(rejectionLog.Rejected(paymentContext.InvoiceId)), shouldRetry: null,
                _ => $"Receiver amount gate did not reject '{name}'. Sender failure: {payTask.Exception?.GetBaseException().Message}", cts.Token).ConfigureAwait(true);
            await AssertEarlyRejectionAsync(tester, paymentContext.InvoiceId, cts.Token).ConfigureAwait(true);
            Assert.False(payTask.IsCompletedSuccessfully, "A rejected original must not result in a signed, broadcast Payjoin proposal.");
            Assert.True((await tester.ExplorerNode.TestMempoolAcceptAsync(original, cancellationToken: cts.Token).WaitAsync(cts.Token).ConfigureAwait(true)).IsAllowed,
                "The mismatched original must be a real broadcastable transaction, so rejection reaches the amount policy.");
            TestLogs.LogInformation($"Receiver rejected broadcastable original {original.GetHash()}: sender output {amountOverride.Satoshi} sats, trusted session amount {Money.Coins(paymentContext.ExpectedDue).Satoshi} sats.");

            senderCancellation.Cancel();
            var senderFailure = await Record.ExceptionAsync(() => payTask).ConfigureAwait(true);
            Assert.NotNull(senderFailure);
            AssertNoReceiverReservations(tester, paymentContext.InvoiceId);
            await AssertReceiverCoinsUnspentAsync(tester, paymentContext.ReceiverOutpointsBeforePayment, cts.Token).ConfigureAwait(true);
            var bridge = await tester.PayTester.GetService<IPayjoinAccountingBridgeService>()
                .TryGetByInvoiceIdAsync(paymentContext.InvoiceId, cts.Token).ConfigureAwait(true);
            Assert.Null(bridge);
            var unpaid = await WaitForInvoiceAsync(tester, paymentContext.InvoiceId,
                entity => entity.GetInvoiceState().Status == InvoiceStatus.New && AccountedPayments(entity, paymentContext.PaymentMethodId).Count == 0,
                cts.Token).ConfigureAwait(true);
            Assert.Empty(AccountedPayments(unpaid, paymentContext.PaymentMethodId));

            if (amountOverride < Money.Coins(paymentContext.ExpectedDue))
            {
                await tester.ExplorerNode.SendRawTransactionAsync(original, cts.Token).ConfigureAwait(true);
                await tester.ExplorerNode.GenerateAsync(1, cts.Token).ConfigureAwait(true);
                var partial = await WaitForInvoiceAsync(tester, paymentContext.InvoiceId, entity =>
                {
                    var state = entity.GetInvoiceState();
                    return state.Status == InvoiceStatus.New && state.ExceptionStatus == InvoiceExceptionStatus.PaidPartial &&
                           AccountedPayments(entity, paymentContext.PaymentMethodId).Sum(item => item.Value) == amountOverride.ToDecimal(MoneyUnit.BTC);
                }, cts.Token).ConfigureAwait(true);
                var senderPayment = Assert.Single(AccountedPayments(partial, paymentContext.PaymentMethodId));
                var handler = tester.PayTester.GetService<PaymentMethodHandlerDictionary>().GetBitcoinHandler(PayjoinConstants.BitcoinCode);
                Assert.Equal(original.GetHash(), senderPayment.GetDetails<BitcoinLikePaymentData>(handler).Outpoint.Hash);
                Assert.True(senderPayment.Value < paymentContext.ExpectedDue);
                await AssertReceiverCoinsUnspentAsync(tester, paymentContext.ReceiverOutpointsBeforePayment, cts.Token).ConfigureAwait(true);
                TestLogs.LogInformation($"Rejected underpayment fallback {original.GetHash()}: {senderPayment.Value} BTC credited, payment {senderPayment.Status}, invoice remains PaidPartial; receiver inputs remain unspent.");
            }
        }
        finally
        {
            senderCancellation.Cancel();
            receiverPassCancellation.Cancel();
            try { await receiverPass.ConfigureAwait(true); } catch (OperationCanceledException) when (receiverPassCancellation.IsCancellationRequested) { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Integration", "Integration")]
    public async Task PreparedConflictSurvivesCheckoutRetryAndHostRestartWhilePlainPaymentWorks(bool disableOutputSubstitution)
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout + TimeSpan.FromMinutes(2));
        using var tester = CreateScenarioTester($"held-{disableOutputSubstitution}");
        tester.DeleteStore = false;
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);
        var payer = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(tester, context.Network, cancellationToken: cts.Token).ConfigureAwait(true);
        await context.Merchant.SetNetworkFeeMode(NetworkFeeMode.Never).WaitAsync(cts.Token).ConfigureAwait(true);
        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);
        var invoice = await PayjoinInvoiceTestHelper.PreparePayjoinInvoiceAsync(tester, context.Merchant, context.Network, cts.Token).ConfigureAwait(true);
        var heldStore = tester.PayTester.GetService<PayjoinReceiverSessionStore>();
        Assert.True(heldStore.TryGetSession(invoice.InvoiceId, out var preparedSession));
        tester.PayTester.GetService<PayjoinPluginDbContextFactory>().SeedBridgeFields(invoice.InvoiceId, bridge => bridge.SettlementScript = "51");
        var prepared = new TaskCompletionSource<Transaction>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var firstCancellation = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var firstSender = new PayjoinTestPayer(tester, payer, context.Network, mineBlockAfterBroadcast: false, disableOutputSubstitution: disableOutputSubstitution);
        var first = firstSender.PayAsync(invoice.PaymentUrl, invoice.OhttpRelayUrls, PayjoinIntegrationTestSupport.TestTimeout,
            Money.Satoshis(4_000_000), tx => prepared.TrySetResult(tx), firstCancellation.Token);
        await firstSender.OriginalPosted.Task.WaitAsync(cts.Token).ConfigureAwait(true);
        var original = await prepared.Task.WaitAsync(cts.Token).ConfigureAwait(true);
        var held = await AssertHeldFailureAsync(tester, invoice.InvoiceId, cts.Token).ConfigureAwait(true);
        AssertSenderPaymentMatches(original, invoice, Money.Satoshis(4_000_000));
        Assert.True((await tester.ExplorerNode.TestMempoolAcceptAsync(original, cancellationToken: cts.Token).WaitAsync(cts.Token).ConfigureAwait(true)).IsAllowed);
        firstCancellation.Cancel();
        Assert.NotNull(await Record.ExceptionAsync(() => first).ConfigureAwait(true));
        Assert.NotNull(held.PayjoinUri);
        var events = held.GetEvents();
        var uri = held.PayjoinUri;
        using (var replay = global::Payjoin.PayjoinMethods.ReplayReceiverEventLog(tester.PayTester.GetService<PayjoinReceiverSessionStore>().CreatePersister(held)))
        using (var state = replay.State())
        {
            var outputs = Assert.IsType<global::Payjoin.ReceiveSession.WantsOutputs>(state);
            Assert.Equal(disableOutputSubstitution, outputs.Inner.OutputSubstitution() == global::Payjoin.OutputSubstitution.Disabled);
        }
        await AssertHeldCheckoutAsync(tester, context.Merchant.StoreId, invoice, events, uri!, cts.Token).ConfigureAwait(true);
        var secondPrepared = new TaskCompletionSource<Transaction>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var secondCancellation = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var secondSender = new PayjoinTestPayer(tester, payer, context.Network, mineBlockAfterBroadcast: false, disableOutputSubstitution: disableOutputSubstitution);
        var second = secondSender.PayAsync(invoice.PaymentUrl, invoice.OhttpRelayUrls, PayjoinIntegrationTestSupport.TestTimeout,
            amountOverride: null, tx => secondPrepared.TrySetResult(tx), secondCancellation.Token);
        await secondSender.OriginalPosted.Task.WaitAsync(cts.Token).ConfigureAwait(true);
        var retryOriginal = await secondPrepared.Task.WaitAsync(cts.Token).ConfigureAwait(true);
        AssertSenderPaymentMatches(retryOriginal, invoice, Money.Satoshis(10_000_000));
        await tester.PayTester.GetService<IEnumerable<Microsoft.Extensions.Hosting.IHostedService>>().OfType<PayjoinReceiverPoller>().Single().ProcessTickOnceAsync(cts.Token).ConfigureAwait(true);
        Assert.False(second.IsCompletedSuccessfully);
        secondCancellation.Cancel();
        Assert.NotNull(await Record.ExceptionAsync(() => second).ConfigureAwait(true));
        await AssertHeldCheckoutAsync(tester, context.Merchant.StoreId, invoice, events, uri!, cts.Token).ConfigureAwait(true);
        await AssertReceiverCoinsUnspentAsync(tester, invoice.ReceiverOutpointsBeforePayment, cts.Token).ConfigureAwait(true);

        var connection = tester.PayTester.Postgres;
        await tester.DisposeAsync().ConfigureAwait(true);
        using var restarted = CreateServerTester(scope: $"held-restart-{Guid.NewGuid():N}", newDb: false);
        restarted.DeleteStore = false;
        restarted.PayTester.Postgres = connection;
        await restarted.StartAsync().WaitAsync(cts.Token).ConfigureAwait(true);
        await restarted.PayTester.GetService<IEnumerable<Microsoft.Extensions.Hosting.IHostedService>>().OfType<PayjoinReceiverPoller>().Single().ProcessTickOnceAsync(cts.Token).ConfigureAwait(true);
        await AssertHeldCheckoutAsync(restarted, context.Merchant.StoreId, invoice, events, uri!, cts.Token).ConfigureAwait(true);
        Assert.True((await restarted.ExplorerNode.TestMempoolAcceptAsync(original, cancellationToken: cts.Token).WaitAsync(cts.Token).ConfigureAwait(true)).IsAllowed,
            "The rejected original must still be unpublished after checkout, retry and restart.");
        var plainId = await restarted.ExplorerNode.SendToAddressAsync(invoice.InvoiceScript.GetDestinationAddress(context.Network.NBitcoinNetwork)!,
            Money.Satoshis(10_000_000), cts.Token).ConfigureAwait(true);
        var block = Assert.Single(await restarted.ExplorerNode.GenerateAsync(1, cts.Token).ConfigureAwait(true));
        var plain = await restarted.ExplorerNode.GetRawTransactionAsync(plainId, block, cancellationToken: cts.Token).ConfigureAwait(true);
        AssertSenderPaymentMatches(plain, invoice, Money.Satoshis(10_000_000));
        Assert.DoesNotContain(plain.Inputs, input => invoice.ReceiverOutpointsBeforePayment.Contains(input.PrevOut.ToString()));
        var paid = await WaitForInvoiceAsync(restarted, invoice.InvoiceId, entity =>
            entity.GetInvoiceState().Status == InvoiceStatus.Settled &&
            AccountedPayments(entity, invoice.PaymentMethodId).Sum(p => p.Value) == 0.1m, cts.Token).ConfigureAwait(true);
        var payment = Assert.Single(AccountedPayments(paid, invoice.PaymentMethodId));
        var handler = restarted.PayTester.GetService<PaymentMethodHandlerDictionary>().GetBitcoinHandler("BTC");
        Assert.Equal(plainId, payment.GetDetails<BitcoinLikePaymentData>(handler).Outpoint.Hash);
        Assert.Equal(0.1m, payment.Value);
        await AssertHeldCheckoutAsync(restarted, context.Merchant.StoreId, invoice, events, uri!, cts.Token, invoiceIsPayable: false).ConfigureAwait(true);
        await AssertReceiverCoinsUnspentAsync(restarted, invoice.ReceiverOutpointsBeforePayment, cts.Token).ConfigureAwait(true);
        AssertNoReceiverReservations(restarted, invoice.InvoiceId);
        TestLogs.LogInformation($"Held payment evidence: modeDisabled={disableOutputSubstitution}; invoice={invoice.InvoiceId}; originalHex={original.ToHex()}; retryOriginalHex={retryOriginal.ToHex()}; plainHex={plain.ToHex()}; creditSats=10000000; status={paid.GetInvoiceState()}; historyEvents={events.Length}; uri={uri}; receiverContribution=0; reservations=0; restarted=true");
        TestLogs.LogInformation($"Held event log: {Newtonsoft.Json.JsonConvert.SerializeObject(events)}");
    }

    private static async Task<PayjoinReceiverSessionState> AssertHeldFailureAsync(ServerTester tester, string invoiceId, CancellationToken cancellationToken)
    {
        var bridges = tester.PayTester.GetService<IPayjoinAccountingBridgeService>();
        await AsyncPolling.WaitUntilAsync(OutcomeTimeout, PollInterval, async token =>
            (await bridges.TryGetByInvoiceIdAsync(invoiceId, token).ConfigureAwait(false))?.Status == PayjoinAccountingBridgeStatus.Failed,
            shouldRetry: null, _ => $"Invoice '{invoiceId}' did not enter Failed", cancellationToken).ConfigureAwait(false);
        Assert.True(tester.PayTester.GetService<PayjoinReceiverSessionStore>().TryGetSession(invoiceId, out var held));
        return Assert.IsType<PayjoinReceiverSessionState>(held);
    }

    private static async Task AssertHeldCheckoutAsync(ServerTester tester, string storeId, PayjoinInvoiceTestHelper.PayjoinInvoiceContext invoice,
        string[] events, string uri, CancellationToken cancellationToken, bool invoiceIsPayable = true)
    {
        var store = tester.PayTester.GetService<PayjoinReceiverSessionStore>();
        var bridges = tester.PayTester.GetService<IPayjoinAccountingBridgeService>();
        var before = await bridges.TryGetByInvoiceIdAsync(invoice.InvoiceId, cancellationToken).ConfigureAwait(false);
        Assert.Equal(PayjoinAccountingBridgeStatus.Failed, before!.Status);
        Assert.Null(await bridges.TryRetryAsync(invoice.InvoiceId, storeId, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false));
        var checkout = await tester.PayTester.GetService<PayjoinInvoicePaymentUrlService>().GetInvoicePaymentUrlAsync(invoice.InvoiceId, cancellationToken).ConfigureAwait(false);
        if (checkout is not null)
        {
            Assert.DoesNotContain("pj=", checkout.Bip21, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.False(invoiceIsPayable, "A payable invoice must still offer ordinary checkout.");
        }
        var settings = await PayjoinIntegrationTestSupport.ReadStoreSettingsAsync(tester, storeId, cancellationToken).ConfigureAwait(false);
        var result = await tester.PayTester.GetService<PayjoinUriSessionService>().BuildAsync("BTC", invoice.InvoiceScript.GetDestinationAddress(Network.RegTest)!.ToString(),
            invoice.ExpectedDue, settings, true, invoice.InvoiceId, storeId, DateTimeOffset.UtcNow.AddHours(1), cancellationToken).ConfigureAwait(false);
        Assert.False(result.Retryable);
        Assert.Equal(PayjoinUriSessionService.ManualRecoveryRequiredReason, result.Reason);
        Assert.DoesNotContain("pj=", result.PaymentUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Null(store.GetServablePayjoinUri(invoice.InvoiceId, invoice.InvoiceScript.GetDestinationAddress(Network.RegTest)!.ToString()));
        Assert.True(store.TryGetSession(invoice.InvoiceId, out var held));
        Assert.Equal(uri, held!.PayjoinUri);
        Assert.Equal(events, held.GetEvents());
        Assert.DoesNotContain(store.GetSessions(), session => session.InvoiceId == invoice.InvoiceId);
        Assert.Equal(before, await bridges.TryGetByInvoiceIdAsync(invoice.InvoiceId, cancellationToken).ConfigureAwait(false));
    }

    private async Task<InvoiceEntity> AssertConfirmedPayjoinAccountingAsync(
        ServerTester tester, PayjoinInvoiceTestHelper.PayjoinInvoiceContext paymentContext,
        PayjoinTestPaymentResult payment, int expectedPaymentCount, CancellationToken cancellationToken, decimal expectedTotal = InvoicePrice,
        bool disableOutputSubstitution = false)
    {
        AssertSenderPaymentMatches(payment.SenderTransaction, paymentContext, Money.Coins(paymentContext.ExpectedDue));
        var block = Assert.Single(await tester.ExplorerNode.GenerateAsync(1, cancellationToken).ConfigureAwait(true));
        var transactionId = uint256.Parse(payment.TransactionId);
        var confirmed = await tester.ExplorerNode.GetRawTransactionAsync(transactionId, block, cancellationToken: cancellationToken).ConfigureAwait(true);
        var contributedInputs = confirmed.Inputs.Where(input => paymentContext.ReceiverOutpointsBeforePayment.Contains(input.PrevOut.ToString())).ToList();
        Assert.NotEmpty(contributedInputs);
        Assert.All(contributedInputs, input => Assert.NotEqual(WitScript.Empty, input.WitScript));
        Assert.Equal(2, confirmed.Outputs.Count);
        Assert.Equal(disableOutputSubstitution, confirmed.Outputs.Any(output => output.ScriptPubKey == paymentContext.InvoiceScript));

        var handler = tester.PayTester.GetService<PaymentMethodHandlerDictionary>().GetBitcoinHandler(PayjoinConstants.BitcoinCode);
        var settled = await WaitForInvoiceAsync(tester, paymentContext.InvoiceId, entity =>
        {
            var payments = AccountedPayments(entity, paymentContext.PaymentMethodId);
            return entity.GetInvoiceState().Status == InvoiceStatus.Settled && payments.Count == expectedPaymentCount &&
                   payments.Sum(item => item.Value) == expectedTotal &&
                   payments.Any(item => item.GetDetails<BitcoinLikePaymentData>(handler).Outpoint.Hash == transactionId && item.Value == paymentContext.ExpectedDue);
        }, cancellationToken).ConfigureAwait(true);
        var finalPayment = Assert.Single(AccountedPayments(settled, paymentContext.PaymentMethodId),
            item => item.GetDetails<BitcoinLikePaymentData>(handler).Outpoint.Hash == transactionId);
        Assert.Equal(paymentContext.ExpectedDue, finalPayment.Value);
        var finalOutpoint = finalPayment.GetDetails<BitcoinLikePaymentData>(handler).Outpoint;
        Assert.True(finalOutpoint.N < confirmed.Outputs.Count);
        var settlement = confirmed.Outputs[(int)finalOutpoint.N];
        Assert.True(settlement.Value > Money.Coins(finalPayment.Value), "The receiver-funded settlement must exceed the amount credited from the sender.");
        var bridge = await tester.PayTester.GetService<IPayjoinAccountingBridgeService>()
            .TryGetByInvoiceIdAsync(paymentContext.InvoiceId, cancellationToken).ConfigureAwait(true);
        Assert.NotNull(bridge);
        Assert.Equal(transactionId.ToString(), bridge.ExpectedFinalTransactionId);
        Assert.Equal(bridge.SettlementScript, Convert.ToHexString(settlement.ScriptPubKey.ToBytes()), ignoreCase: true);
        Assert.Equal(settlement.Value.Satoshi, bridge.ExpectedFinalValueSats);
        Assert.Equal(Money.Coins(paymentContext.ExpectedDue).Satoshi, bridge.EffectiveInvoiceValueSats);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyRemovedAsync(tester, paymentContext.InvoiceId, cancellationToken).ConfigureAwait(true);
        var reconciled = await tester.PayTester.GetService<IPayjoinAccountingBridgeService>().TryGetByInvoiceIdAsync(paymentContext.InvoiceId, cancellationToken).ConfigureAwait(true);
        Assert.Equal(PayjoinAccountingBridgeStatus.Reconciled, reconciled!.Status);
        AssertNoReceiverReservations(tester, paymentContext.InvoiceId);
        TestLogs.LogInformation($"Evidence settled: invoice {paymentContext.InvoiceId}, state {settled.GetInvoiceState()}, payment {finalPayment.Value} BTC/{finalPayment.Status}, reservations 0; original hex {payment.SenderTransaction.ToHex()}; final hex {confirmed.ToHex()}");
        TestLogs.LogInformation($"Original {payment.SenderTransaction.GetHash()} paid {paymentContext.ExpectedDue} BTC; confirmed Payjoin {transactionId} spends receiver input(s) {string.Join(", ", contributedInputs.Select(input => input.PrevOut))} with witness; settlement {finalOutpoint} is {settlement.Value.Satoshi} sats, invoice credit is {finalPayment.Value} BTC.");
        return settled;
    }

    private static List<PaymentEntity> AccountedPayments(InvoiceEntity invoice, PaymentMethodId paymentMethodId) =>
        invoice.GetPayments(false).Where(payment => payment.Accounted && payment.PaymentMethodId == paymentMethodId).ToList();

    private static async Task<PayjoinInvoiceTestHelper.PayjoinInvoiceContext> ReplaceWithShortNativeSessionAsync(
        ServerTester tester, string storeId, PayjoinInvoiceTestHelper.PayjoinInvoiceContext invoice, ulong seconds, CancellationToken token)
    {
        var sessions = tester.PayTester.GetService<PayjoinReceiverSessionStore>();
        Assert.True(sessions.TryGetSession(invoice.InvoiceId, out var old));
        Assert.NotNull(old);
        var settings = await PayjoinIntegrationTestSupport.ReadStoreSettingsAsync(tester, storeId, token).ConfigureAwait(false);
        var route = await tester.PayTester.GetService<PayjoinMailroomManager>().SelectBootstrapRouteAsync(settings, storeId, invoice.InvoiceId, token).ConfigureAwait(false);
        Assert.NotNull(route);
        using var keys = route.OhttpKeys;
        using var builder = new global::Payjoin.ReceiverBuilder(old.ReceiverAddress, route.DirectoryUrl.AbsoluteUri, keys);
        using var pinned = builder.WithAmount(checked((ulong)Money.Coins(invoice.ExpectedDue).Satoshi));
        using var expiring = pinned.WithExpiration(seconds);
        using var transition = expiring.Build();
        var capture = new CapturingReceiverSessionPersister();
        using var initialized = transition.Save(capture);
        using var uri = initialized.PjUri();
        Assert.True(sessions.RemoveSession(invoice.InvoiceId, old.SessionInstanceId));
        var created = sessions.GetOrCreateSession(invoice.InvoiceId, old.ReceiverAddress, storeId, old.MonitoringExpiresAt, capture.Events,
            new CreatePayjoinAccountingBridgeRequest(invoice.InvoiceId, storeId, "BTC", invoice.PaymentMethodId.ToString(), old.MonitoringExpiresAt));
        sessions.StorePayjoinUri(invoice.InvoiceId, old.ReceiverAddress, uri.AsString(), created.SessionInstanceId);
        return invoice with { PaymentUrl = new Uri(uri.AsString(), UriKind.Absolute) };
    }

    private static Task WaitForNativeExpiryAsync(string[] events, CancellationToken token) =>
        AsyncPolling.WaitUntilAsync(TimeSpan.FromSeconds(90), PollInterval, _ =>
        {
            try
            {
                using var replay = global::Payjoin.PayjoinMethods.ReplayReceiverEventLog(new PrefixPersister(events));
                return Task.FromResult(false);
            }
            catch (global::Payjoin.ReceiverReplayException ex)
            {
                try { Assert.True(ex.IsExpired(), ex.Message); return Task.FromResult(true); }
                finally { ex.Dispose(); }
            }
        }, shouldRetry: null, _ => "Native receiver did not report IsExpired()", token);

    private static PSBT RecoverSignedProposal(string[] events, Network network)
    {
        for (var count = 1; count <= events.Length; count++)
        {
            using var replay = global::Payjoin.PayjoinMethods.ReplayReceiverEventLog(new PrefixPersister(events[..count]));
            using var state = replay.State();
            if (state is global::Payjoin.ReceiveSession.PayjoinProposal proposal)
                return PSBT.Parse(proposal.Inner.Psbt(), network);
        }
        throw new InvalidOperationException("The saved event log did not contain a signed proposal.");
    }

    private sealed class PrefixPersister(string[] events) : global::Payjoin.JsonReceiverSessionPersister
    {
        public string[] Load() => events;
        public void Save(string @event) => throw new NotSupportedException();
        public void Close() { }
    }

    private static async Task<InvoiceEntity> WaitForInvoiceAsync(
        ServerTester tester, string invoiceId, Func<InvoiceEntity, bool> predicate, CancellationToken cancellationToken)
    {
        InvoiceEntity? latest = null;
        await AsyncPolling.WaitUntilAsync(OutcomeTimeout, PollInterval, async token =>
        {
            latest = await tester.PayTester.GetService<InvoiceRepository>().GetInvoice(invoiceId).WaitAsync(token).ConfigureAwait(false);
            return latest is not null && predicate(latest);
        }, shouldRetry: null, _ => $"Invoice '{invoiceId}' did not reach the expected accounting state; current state: {latest?.GetInvoiceState()}.", cancellationToken).ConfigureAwait(false);
        Assert.NotNull(latest);
        return latest;
    }

    private static void AssertNoReceiverReservations(ServerTester tester, string invoiceId)
    {
        using var database = tester.PayTester.GetService<PayjoinPluginDbContextFactory>().CreateContext();
        Assert.Empty(database.ReceiverInputReservations.Where(reservation => reservation.InvoiceId == invoiceId).ToArray());
    }

    private async Task AssertReceiverCoinsUnspentAsync(ServerTester tester, HashSet<string> receiverOutpoints, CancellationToken cancellationToken)
    {
        foreach (var text in receiverOutpoints)
        {
            var outpoint = OutPoint.Parse(text);
            var unspent = await tester.ExplorerNode.GetTxOutAsync(outpoint.Hash, checked((int)outpoint.N), includeMempool: true, cancellationToken).ConfigureAwait(true);
            Assert.NotNull(unspent);
            TestLogs.LogInformation($"Evidence receiver UTXO preserved: {outpoint}, {unspent.TxOut.Value.Satoshi} sats, script {unspent.TxOut.ScriptPubKey.ToHex()}");
        }
    }

    private static void AssertSenderPaymentMatches(Transaction senderTransaction, PayjoinInvoiceTestHelper.PayjoinInvoiceContext paymentContext, Money expected)
    {
        var output = Assert.Single(senderTransaction.Outputs, item => item.ScriptPubKey == paymentContext.InvoiceScript);
        Assert.Equal(expected, output.Value);
    }

    private sealed class OutputFailurePoller(ServerTester tester, string? failure) : IPayjoinReceiverOutputBuilder, IAsyncDisposable
    {
        private PayjoinReceiverPoller? _poller;
        public bool Enabled { get; set; } = true;
        public int Calls { get; private set; }
        public int TotalCalls { get; private set; }
        public PayjoinAccountingBridgeState? Attached { get; private set; }

        public async Task StartAsync(CancellationToken token, IPayjoinReceiverInputSelector? inputs = null,
            IPayjoinReceiverProposalFinalizer? finalizer = null, IPayjoinOriginalTransactionValidator? validator = null)
        {
            var existing = tester.PayTester.GetService<IEnumerable<Microsoft.Extensions.Hosting.IHostedService>>().OfType<PayjoinReceiverPoller>().Single();
            await existing.StopAsync(token).ConfigureAwait(false);
            var sessions = tester.PayTester.GetService<PayjoinReceiverSessionStore>();
            var bridges = tester.PayTester.GetService<IPayjoinAccountingBridgeService>();
            var payments = tester.PayTester.GetService<IPayjoinAccountingPaymentService>();
            validator ??= tester.PayTester.GetService<IPayjoinOriginalTransactionValidator>();
            var stateProcessor = new PayjoinReceiverStateProcessor(sessions,
                tester.PayTester.GetService<IPayjoinReceiverRelayRequestSender>(),
                tester.PayTester.GetService<IPayjoinWalletOwnershipService>(),
                tester.PayTester.GetService<PayjoinSeenInputStore>(), tester.PayTester.GetService<PayjoinMailroomManager>(),
                validator, tester.PayTester.GetService<IPayjoinReceiverSessionGuard>());
            var processor = new PayjoinReceiverSessionProcessor(sessions,
                tester.PayTester.GetService<IPayjoinReceiverSessionGuard>(),
                stateProcessor, this,
                inputs ?? tester.PayTester.GetService<IPayjoinReceiverInputSelector>(), bridges,
                finalizer ?? tester.PayTester.GetService<IPayjoinReceiverProposalFinalizer>(),
                tester.PayTester.GetService<BTCPayNetworkProvider>(),
                tester.PayTester.GetService<ILogger<PayjoinReceiverSessionProcessor>>(),
                validator);
            _poller = new PayjoinReceiverPoller(sessions, processor, bridges, payments,
                tester.PayTester.GetService<ILogger<PayjoinReceiverPoller>>());
            await _poller.StartAsync(token).ConfigureAwait(false);
        }

        public async Task<PayjoinReceiverOutputBuilder.OutputBuildResult> TryCreateSettlementOutputsAsync(string storeId, string invoiceId,
            byte[] receiverScript, bool preserveReceiverScript, long? pinnedSettlementAmountSats, CancellationToken cancellationToken)
        {
            TotalCalls++;
            if (!Enabled) return await tester.PayTester.GetService<IPayjoinReceiverOutputBuilder>().TryCreateSettlementOutputsAsync(
                storeId, invoiceId, receiverScript, preserveReceiverScript, pinnedSettlementAmountSats, cancellationToken).ConfigureAwait(false);
            Calls++;
            Attached = await tester.PayTester.GetService<IPayjoinAccountingBridgeService>().TryGetByInvoiceIdAsync(invoiceId, cancellationToken).ConfigureAwait(false);
            return failure == "InvalidDestination"
                ? new(PayjoinReceiverOutputBuilder.OutputBuildStatus.InvalidDestination)
                : new(PayjoinReceiverOutputBuilder.OutputBuildStatus.Ready,
                    new PayjoinReceiverOutputBuilder.OutputReplacement([], [0x51], checked((ulong)pinnedSettlementAmountSats!.Value), null));
        }

        public async ValueTask DisposeAsync()
        {
            if (_poller is null) return;
            await _poller.StopAsync(CancellationToken.None).ConfigureAwait(false);
            _poller.Dispose();
        }
    }

    private sealed class ContributedInputOutage(ServerTester tester) : IPayjoinReceiverInputSelector, IPayjoinReceiverProposalFinalizer
    {
        private readonly IPayjoinReceiverInputSelector _inputs = tester.PayTester.GetService<IPayjoinReceiverInputSelector>();
        private readonly IPayjoinReceiverProposalFinalizer _finalizer = tester.PayTester.GetService<IPayjoinReceiverProposalFinalizer>();
        public volatile bool Available;
        public int Contributions { get; private set; }
        public int Finalizations { get; private set; }
        public int Posts { get; private set; }
        public ConcurrentQueue<string> Lookups { get; } = new();

        public Task<ReceiverInputContributionResult> TryContributeInputsAsync(global::Payjoin.WantsInputs proposal, string storeId,
            DateTimeOffset expires, PayjoinReceiverSessionStore.DatabaseBackedReceiverPersister owner, CancellationToken token)
        {
            Contributions++;
            return _inputs.TryContributeInputsAsync(proposal, storeId, expires, owner, token);
        }

        public Task<BTCPayServer.Services.Wallets.ReceivedCoin[]?> TryGetPersistedContributedCoinsAsync(PayjoinReceiverSessionState session, CancellationToken token)
        {
            Assert.True(session.TryGetContributedInput(out var outpoint));
            Lookups.Enqueue(outpoint!.ToString());
            return Available ? _inputs.TryGetPersistedContributedCoinsAsync(session, token) : Task.FromResult<BTCPayServer.Services.Wallets.ReceivedCoin[]?>(null);
        }

        public Task FinalizeAsync(PayjoinReceiverProposalFinalizationContext context, global::Payjoin.WantsFeeRange proposal, BTCPayServer.Services.Wallets.ReceivedCoin[] coins, CancellationToken token)
        {
            if (!Available) return Task.CompletedTask;
            Finalizations++;
            return _finalizer.FinalizeAsync(context, proposal, coins, token);
        }

        public Task FinalizeAsync(PayjoinReceiverProposalFinalizationContext context, global::Payjoin.ProvisionalProposal proposal, BTCPayServer.Services.Wallets.ReceivedCoin[] coins, CancellationToken token)
        {
            Finalizations++;
            return _finalizer.FinalizeAsync(context, proposal, coins, token);
        }

        public Task EnsureExpectedFinalTransactionAsync(PayjoinReceiverProposalFinalizationContext context, global::Payjoin.IPayjoinProposal proposal, CancellationToken token) => _finalizer.EnsureExpectedFinalTransactionAsync(context, proposal, token);
        public Task PostAsync(PayjoinReceiverProposalFinalizationContext context, global::Payjoin.IPayjoinProposal proposal, CancellationToken token)
        {
            Posts++;
            return _finalizer.PostAsync(context, proposal, token);
        }
    }

    private sealed class RecordingBroadcastValidator(IPayjoinOriginalTransactionValidator inner) : IPayjoinOriginalTransactionValidator
    {
        public volatile bool Reject;
        public Func<Task>? BeforeResult { get; set; }
        public ConcurrentQueue<OriginalTransactionValidation> Results { get; } = new();
        public async Task<OriginalTransactionValidation> ValidateAsync(byte[] original, CancellationToken cancellationToken)
        {
            var result = Reject ? new OriginalTransactionValidation(OriginalTransactionVerdict.Rejected, "injected replay rejection")
                : await inner.ValidateAsync(original, cancellationToken).ConfigureAwait(false);
            if (BeforeResult is { } callback)
            {
                BeforeResult = null;
                await callback().ConfigureAwait(false);
            }
            Results.Enqueue(result);
            return result;
        }
    }

    private sealed class FailingLoadFactory(string connectionString) : PayjoinPluginDbContextFactory(
        Microsoft.Extensions.Options.Options.Create(new BTCPayServer.Abstractions.Models.DatabaseOptions { ConnectionString = connectionString }))
    {
        public int Failures { get; private set; }
        public override PayjoinPluginDbContext CreateContext(Action<Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder>? configure = null)
        {
            var builder = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<PayjoinPluginDbContext>();
            ConfigureBuilder(builder, configure);
            builder.AddInterceptors(new LoadFailure(this));
            return new PayjoinPluginDbContext(builder.Options);
        }
        private sealed class LoadFailure(FailingLoadFactory owner) : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
        {
            public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(
                System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
                Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result)
            {
                if (command.CommandText.Contains("ReceiverSessions", StringComparison.Ordinal) && owner.Failures == 0)
                {
                    owner.Failures++;
                    throw new System.IO.IOException("Injected temporary event-log load failure after signed proposal commit");
                }
                return result;
            }
        }
    }

    private sealed class ReceiverAmountRejectionLog : ILoggerProvider, ILogger
    {
        private readonly ConcurrentQueue<string> _rejections = new();

        public bool Rejected(string invoiceId) => _rejections.Any(message =>
            message.Contains(invoiceId, StringComparison.Ordinal) &&
            message.Contains("original payment amount does not match the pinned invoice amount", StringComparison.Ordinal));

        public ILogger CreateLogger(string categoryName) => categoryName == typeof(PayjoinReceiverSessionProcessor).FullName ? this : NullLogger.Instance;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 19)
            {
                _rejections.Enqueue(formatter(state, exception));
            }
        }

        public void Dispose()
        {
        }
    }
}
