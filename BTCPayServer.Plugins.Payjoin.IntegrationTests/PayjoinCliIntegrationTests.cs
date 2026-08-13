using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using BTCPayServer.Tests;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class PayjoinCliIntegrationTests : UnitTestBase
{
    private const string OriginalPsbtRejectedMarker = "The receiver rejected the original PSBT.";
    private static readonly TimeSpan CliTestTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PreSendReceiverDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ShortInvoiceLifetime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ShortMonitoringLifetime = TimeSpan.FromSeconds(1);
    private static readonly Uri UnavailableRelayUrl = new("https://127.0.0.1:1/");

    public PayjoinCliIntegrationTests(ITestOutputHelper helper) : base(helper)
    {
    }

    [Fact(Explicit = true)]
    [Trait("Integration", "Integration")]
    public async Task CreateInvoiceAndPayItThroughThePayjoinPluginWithPayjoinCli()
    {
        using var cts = new CancellationTokenSource(CliTestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var paymentResult = await PayjoinCliIntegrationTestSupport.CreateAndPayInvoiceWithInvoiceIdAsync(
            tester,
            context.Merchant,
            context.Network,
            preSendReceiverPollDelay: TimeSpan.Zero,
            cancellationToken: cts.Token).ConfigureAwait(true);

        PayjoinIntegrationTestSupport.AssertSuccessfulPayjoinTransaction((paymentResult.PayjoinTransaction, paymentResult.InvoiceScript, paymentResult.TransactionId));
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyRemovedAsync(tester, paymentResult.InvoiceId, cts.Token).ConfigureAwait(true);
    }

    [Fact(Explicit = true)]
    [Trait("Integration", "Integration")]
    public async Task CreateInvoiceAndPayItThroughThePayjoinPluginWithPayjoinCliWhenSenderPostsAfterReceiverPollDelay()
    {
        using var cts = new CancellationTokenSource(CliTestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var paymentResult = await PayjoinCliIntegrationTestSupport.CreateAndPayInvoiceWithInvoiceIdAsync(
            tester,
            context.Merchant,
            context.Network,
            preSendReceiverPollDelay: PreSendReceiverDelay,
            cancellationToken: cts.Token).ConfigureAwait(true);

        PayjoinIntegrationTestSupport.AssertSuccessfulPayjoinTransaction((paymentResult.PayjoinTransaction, paymentResult.InvoiceScript, paymentResult.TransactionId));
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyRemovedAsync(tester, paymentResult.InvoiceId, cts.Token).ConfigureAwait(true);
    }

    [Fact(Explicit = true)]
    [Trait("Integration", "Integration")]
    public async Task CreateInvoiceAndPayItThroughThePayjoinPluginWithPayjoinCliAcrossConfiguredRelays()
    {
        using var cts = new CancellationTokenSource(CliTestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, settings =>
        {
            var configuredRelays = settings.GetEffectiveOhttpRelayUrls();
            settings.OhttpRelayUrls = [UnavailableRelayUrl, .. configuredRelays];
        }, cts.Token).ConfigureAwait(true);

        var paymentResult = await PayjoinCliIntegrationTestSupport.CreateAndPayInvoiceWithInvoiceIdAsync(
            tester,
            context.Merchant,
            context.Network,
            preSendReceiverPollDelay: TimeSpan.Zero,
            cancellationToken: cts.Token).ConfigureAwait(true);

        PayjoinIntegrationTestSupport.AssertSuccessfulPayjoinTransaction((paymentResult.PayjoinTransaction, paymentResult.InvoiceScript, paymentResult.TransactionId));
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyRemovedAsync(tester, paymentResult.InvoiceId, cts.Token).ConfigureAwait(true);
    }

    [Fact(Explicit = true)]
    [Trait("Integration", "Integration")]
    public async Task PayjoinCliDoesNotBroadcastWhenAllSenderRelaysAreUnavailable()
    {
        using var cts = new CancellationTokenSource(CliTestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);
        var payjoinContext = await PayjoinInvoiceTestHelper.PreparePayjoinInvoiceAsync(
            tester,
            context.Merchant,
            context.Network,
            cts.Token).ConfigureAwait(true);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(
            tester,
            payjoinContext.InvoiceId,
            cts.Token).ConfigureAwait(true);

        using var senderWallet = await PayjoinCliSenderWallet.CreateInitializedAsync(
            tester,
            context.Network,
            cts.Token).ConfigureAwait(true);
        using var payjoinCliPayer = new PayjoinCliPayer(senderWallet);
        var failure = await payjoinCliPayer.PayExpectingFailureAsync(
            payjoinContext.PaymentUrl,
            [UnavailableRelayUrl],
            payjoinContext.InvoiceScript,
            cts.Token).ConfigureAwait(true);

        var diagnostics = $"{failure.StandardOutput}\n{failure.StandardError}";
        Assert.Contains("No valid relays available", diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(failure.SessionId));
        var senderSessionId = failure.SessionId!;

        var history = await payjoinCliPayer.GetHistoryAsync(
            [UnavailableRelayUrl],
            cts.Token).ConfigureAwait(true);
        Assert.Contains(senderSessionId, history.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Waiting for proposal", history.StandardOutput, StringComparison.Ordinal);

        var cancellation = await payjoinCliPayer.CancelSessionWithoutBroadcastAsync(
            senderSessionId,
            [UnavailableRelayUrl],
            payjoinContext.InvoiceScript,
            cts.Token).ConfigureAwait(true);

        var repeatedCancellation = await payjoinCliPayer.CancelAlreadyCancelledSessionWithoutBroadcastAsync(
            senderSessionId,
            [UnavailableRelayUrl],
            payjoinContext.InvoiceScript,
            cts.Token).ConfigureAwait(true);
        Assert.Equal(cancellation.ToHex(), repeatedCancellation.ToHex());

        var receiverSession = PayjoinReceiverTestHelper.GetRequiredReceiverSession(
            tester,
            payjoinContext.InvoiceId);
        Assert.False(receiverSession.IsCloseRequested);
        Assert.False(receiverSession.TryGetContributedInput(out _));
    }

    [Fact(Explicit = true)]
    [Trait("Integration", "Integration")]
    public async Task PayjoinCliIsRejectedWhenInvoiceIsAlreadyInvalid()
    {
        using var cts = new CancellationTokenSource(CliTestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);
        using var senderWallet = await PayjoinCliSenderWallet.CreateInitializedAsync(
            tester,
            context.Network,
            cts.Token).ConfigureAwait(true);

        var payjoinContext = await PayjoinInvoiceTestHelper.PreparePayjoinInvoiceAsync(
            tester,
            context.Merchant,
            context.Network,
            cts.Token).ConfigureAwait(true);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(
            tester,
            payjoinContext.InvoiceId,
            cts.Token).ConfigureAwait(true);

        var invoiceRepository = tester.PayTester.GetService<InvoiceRepository>();
        var marked = await invoiceRepository
            .MarkInvoiceStatus(payjoinContext.InvoiceId, InvoiceStatus.Invalid)
            .WaitAsync(cts.Token)
            .ConfigureAwait(true);
        Assert.True(marked);
        await PayjoinInvoiceTestHelper.AssertInvoiceStatusEventuallyAsync(
            tester,
            payjoinContext.InvoiceId,
            InvoiceStatus.Invalid,
            cts.Token).ConfigureAwait(true);
        await AssertPayjoinCliRejectedAndReceiverSessionRemovedAsync(
            tester,
            senderWallet,
            payjoinContext,
            InvoiceStatus.Invalid,
            cts.Token).ConfigureAwait(true);
    }

    [Fact(Explicit = true)]
    [Trait("Integration", "Integration")]
    public async Task PayjoinCliIsRejectedWhenInvoiceIsAlreadyExpired()
    {
        using var cts = new CancellationTokenSource(CliTestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);
        using var senderWallet = await PayjoinCliSenderWallet.CreateInitializedAsync(
            tester,
            context.Network,
            cts.Token).ConfigureAwait(true);

        var payjoinContext = await PayjoinInvoiceTestHelper.PreparePayjoinInvoiceAsync(
            tester,
            context.Merchant,
            context.Network,
            cts.Token).ConfigureAwait(true);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(
            tester,
            payjoinContext.InvoiceId,
            cts.Token).ConfigureAwait(true);

        var invoiceRepository = tester.PayTester.GetService<InvoiceRepository>();
        await invoiceRepository
            .UpdateInvoiceExpiry(payjoinContext.InvoiceId, TimeSpan.Zero)
            .WaitAsync(cts.Token)
            .ConfigureAwait(true);
        await PayjoinInvoiceTestHelper.AssertInvoiceStatusEventuallyAsync(
            tester,
            payjoinContext.InvoiceId,
            InvoiceStatus.Expired,
            cts.Token).ConfigureAwait(true);
        await AssertPayjoinCliRejectedAndReceiverSessionRemovedAsync(
            tester,
            senderWallet,
            payjoinContext,
            InvoiceStatus.Expired,
            cts.Token).ConfigureAwait(true);
    }

    [Fact(Explicit = true)]
    [Trait("Integration", "Integration")]
    public async Task PayjoinCliIsRejectedWhenInvoiceIsAlreadyPaid()
    {
        using var cts = new CancellationTokenSource(CliTestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);
        using var senderWallet = await PayjoinCliSenderWallet.CreateInitializedAsync(
            tester,
            context.Network,
            cts.Token).ConfigureAwait(true);

        var payjoinContext = await PayjoinInvoiceTestHelper.PreparePayjoinInvoiceAsync(
            tester,
            context.Merchant,
            context.Network,
            cts.Token).ConfigureAwait(true);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(
            tester,
            payjoinContext.InvoiceId,
            cts.Token).ConfigureAwait(true);

        await context.Merchant.PayInvoice(payjoinContext.InvoiceId).WaitAsync(cts.Token).ConfigureAwait(true);
        await context.Merchant.WaitInvoicePaid(payjoinContext.InvoiceId).WaitAsync(cts.Token).ConfigureAwait(true);
        await AssertPayjoinCliRejectedAndReceiverSessionRemovedAsync(
            tester,
            senderWallet,
            payjoinContext,
            InvoiceStatus.Processing,
            cts.Token).ConfigureAwait(true);
    }

    [Fact(Explicit = true)]
    [Trait("Integration", "Integration")]
    public async Task PayjoinCliSendsPluginChangeToConfiguredColdWallet()
    {
        using var cts = new CancellationTokenSource(CliTestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        var coldDerivation = await PayjoinIntegrationTestSupport.CreateTrackedColdWalletAsync(tester, cts.Token).ConfigureAwait(true);
        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, settings =>
        {
            settings.ColdWalletDerivationScheme = coldDerivation.ToString();
        }, cts.Token).ConfigureAwait(true);

        var paymentResult = await PayjoinCliIntegrationTestSupport.CreateAndPayInvoiceWithInvoiceIdAsync(
            tester,
            context.Merchant,
            context.Network,
            preSendReceiverPollDelay: TimeSpan.Zero,
            cancellationToken: cts.Token).ConfigureAwait(true);

        PayjoinIntegrationTestSupport.AssertSuccessfulPayjoinTransaction((paymentResult.PayjoinTransaction, paymentResult.InvoiceScript, paymentResult.TransactionId));
        await PayjoinIntegrationTestSupport.AssertColdWalletReceivedPayjoinChangeAsync(
            tester,
            coldDerivation,
            (paymentResult.PayjoinTransaction, paymentResult.InvoiceScript, paymentResult.TransactionId),
            cts.Token).ConfigureAwait(true);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyRemovedAsync(
            tester,
            paymentResult.InvoiceId,
            cts.Token).ConfigureAwait(true);
    }

    [Fact(Explicit = true)]
    [Trait("Integration", "Integration")]
    public async Task PayjoinCliInvoiceTransitionsFromProcessingToSettledAfterConfirmation()
    {
        using var cts = new CancellationTokenSource(CliTestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);
        var payjoinContext = await PayjoinInvoiceTestHelper.PreparePayjoinInvoiceAsync(
            tester,
            context.Merchant,
            context.Network,
            cts.Token).ConfigureAwait(true);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(
            tester,
            payjoinContext.InvoiceId,
            cts.Token).ConfigureAwait(true);

        using var senderWallet = await PayjoinCliSenderWallet.CreateInitializedAsync(
            tester,
            context.Network,
            cts.Token).ConfigureAwait(true);
        using var payjoinCliPayer = new PayjoinCliPayer(senderWallet);
        var cliPayment = await payjoinCliPayer.PayAsync(
            payjoinContext.PaymentUrl,
            payjoinContext.OhttpRelayUrls,
            payjoinContext.InvoiceScript,
            cts.Token).ConfigureAwait(true);
        await PayjoinCliIntegrationTestSupport.AssertSuccessfulSenderSessionStateAsync(
            payjoinCliPayer,
            cliPayment,
            payjoinContext.OhttpRelayUrls,
            cts.Token).ConfigureAwait(true);

        await PayjoinInvoiceTestHelper.AssertInvoiceProcessingThenSettledAsync(
            tester,
            payjoinContext.InvoiceId,
            async cancellationToken =>
            {
                await tester.ExplorerNode.GenerateAsync(1, cancellationToken).ConfigureAwait(true);
            },
            cts.Token).ConfigureAwait(true);

        var paymentResult = await PayjoinInvoiceTestHelper.FinalizePayjoinPaymentAsync(
            tester,
            context.Merchant,
            payjoinContext,
            cliPayment.TransactionId,
            cts.Token).ConfigureAwait(true);
        PayjoinIntegrationTestSupport.AssertSuccessfulPayjoinTransaction(paymentResult);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyRemovedAsync(
            tester,
            payjoinContext.InvoiceId,
            cts.Token).ConfigureAwait(true);
    }

    [Fact(Explicit = true)]
    [Trait("Integration", "Integration")]
    public async Task PayjoinCliLeavesFallbackWhenPluginReceiverSessionExpires()
    {
        using var cts = new CancellationTokenSource(CliTestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);
        using var senderWallet = await PayjoinCliSenderWallet.CreateInitializedAsync(
            tester,
            context.Network,
            cts.Token).ConfigureAwait(true);

        await ConfigureExpiryTestLifetimeAsync(
            tester,
            context.Merchant.StoreId,
            invoiceLifetime: ShortInvoiceLifetime,
            monitoringLifetime: ShortMonitoringLifetime,
            cancellationToken: cts.Token).ConfigureAwait(true);

        var receiverPoller = tester.PayTester.GetService<IEnumerable<IHostedService>>()
            .OfType<PayjoinReceiverPoller>()
            .Single();
        await receiverPoller.StopAsync(cts.Token).ConfigureAwait(true);

        var payjoinContext = await PayjoinInvoiceTestHelper.PreparePayjoinInvoiceAsync(
            tester,
            context.Merchant,
            context.Network,
            cts.Token).ConfigureAwait(true);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(
            tester,
            payjoinContext.InvoiceId,
            cts.Token).ConfigureAwait(true);

        using var payjoinCliPayer = new PayjoinCliPayer(senderWallet);
        var expiryResult = await payjoinCliPayer.PayExpectingExpiryAsync(
            payjoinContext.PaymentUrl,
            payjoinContext.OhttpRelayUrls,
            payjoinContext.InvoiceScript,
            cts.Token).ConfigureAwait(true);

        var history = await payjoinCliPayer.GetHistoryAsync(
            payjoinContext.OhttpRelayUrls,
            cts.Token).ConfigureAwait(true);
        Assert.Contains(expiryResult.SessionId, history.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Session expired at", history.StandardOutput, StringComparison.Ordinal);

        var repeatedCancellation = await payjoinCliPayer.CancelExpiredSessionAgainWithoutBroadcastAsync(
            expiryResult.SessionId,
            payjoinContext.OhttpRelayUrls,
            cts.Token).ConfigureAwait(true);
        Assert.Equal(expiryResult.FallbackTransaction.ToHex(), repeatedCancellation.ToHex());

        await PayjoinInvoiceTestHelper.AssertInvoiceStatusEventuallyAsync(
            tester,
            payjoinContext.InvoiceId,
            InvoiceStatus.Expired,
            cts.Token).ConfigureAwait(true);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCloseRequestedAsync(
            tester,
            payjoinContext.InvoiceId,
            cts.Token).ConfigureAwait(true);

        await receiverPoller.ProcessTickOnceAsync(cts.Token).ConfigureAwait(true);
        var sessionStore = tester.PayTester.GetService<PayjoinReceiverSessionStore>();
        Assert.False(
            sessionStore.TryGetSession(payjoinContext.InvoiceId, out _),
            $"Expected the manual receiver poll tick to remove expired session '{payjoinContext.InvoiceId}'.");
    }

    private static async Task AssertPayjoinCliRejectedAsync(
        PayjoinCliPayer payjoinCliPayer,
        PayjoinInvoiceTestHelper.PayjoinInvoiceContext payjoinContext,
        CancellationToken cancellationToken)
    {
        var failure = await payjoinCliPayer.PayExpectingFailureAsync(
            payjoinContext.PaymentUrl,
            payjoinContext.OhttpRelayUrls,
            payjoinContext.InvoiceScript,
            cancellationToken).ConfigureAwait(true);

        var diagnostics = $"{failure.StandardOutput}\n{failure.StandardError}";
        Assert.Contains(OriginalPsbtRejectedMarker, diagnostics, StringComparison.Ordinal);
    }

    private static async Task AssertPayjoinCliRejectedAndReceiverSessionRemovedAsync(
        ServerTester tester,
        PayjoinCliSenderWallet senderWallet,
        PayjoinInvoiceTestHelper.PayjoinInvoiceContext payjoinContext,
        InvoiceStatus expectedCloseStatus,
        CancellationToken cancellationToken)
    {
        using var payjoinCliPayer = new PayjoinCliPayer(senderWallet);
        var closeRequestedTask = PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCloseRequestedAsync(
            tester,
            payjoinContext.InvoiceId,
            cancellationToken);
        var rejectionTask = AssertPayjoinCliRejectedAsync(
            payjoinCliPayer,
            payjoinContext,
            cancellationToken);

        await Task.WhenAll(rejectionTask, closeRequestedTask).ConfigureAwait(true);
        var receiverSession = await closeRequestedTask.ConfigureAwait(true);
        Assert.Equal(expectedCloseStatus, receiverSession.CloseInvoiceStatus);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyRemovedAsync(
            tester,
            payjoinContext.InvoiceId,
            cancellationToken).ConfigureAwait(true);
    }

    private static async Task ConfigureExpiryTestLifetimeAsync(
        ServerTester tester,
        string storeId,
        TimeSpan invoiceLifetime,
        TimeSpan monitoringLifetime,
        CancellationToken cancellationToken)
    {
        var storeRepository = tester.PayTester.GetService<StoreRepository>();
        var store = await storeRepository.FindStore(storeId).WaitAsync(cancellationToken).ConfigureAwait(true);
        Assert.NotNull(store);

        var storeBlob = store.GetStoreBlob();
        storeBlob.InvoiceExpiration = invoiceLifetime;
        storeBlob.MonitoringExpiration = monitoringLifetime;
        Assert.True(store.SetStoreBlob(storeBlob));

        await storeRepository.UpdateStore(store).WaitAsync(cancellationToken).ConfigureAwait(true);
    }
}
