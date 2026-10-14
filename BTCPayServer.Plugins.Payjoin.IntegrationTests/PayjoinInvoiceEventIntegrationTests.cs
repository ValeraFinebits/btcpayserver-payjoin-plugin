using BTCPayServer.Client.Models;
using BTCPayServer.Controllers;
using BTCPayServer.Data;
using BTCPayServer.Models.InvoicingModels;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Tests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class PayjoinInvoiceEventIntegrationTests : UnitTestBase
{
    private const string ExpectedTransactionId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly TimeSpan JournalTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    public PayjoinInvoiceEventIntegrationTests(ITestOutputHelper helper) : base(helper)
    {
    }

    [Theory]
    [InlineData("unpaid")]
    [InlineData("plain-bitcoin-paid")]
    [InlineData("unresolved-final")]
    [Trait("Integration", "Integration")]
    public async Task ExpiredBridgeJournalDoesNotInventPaymentOrProtocolOutcomes(string scenario)
    {
        using var cancellation = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester("InvoiceJournalExpiry-" + scenario + "-" + Guid.NewGuid().ToString("N"), newDb: true);
        await tester.StartAsync().WaitAsync(cancellation.Token).ConfigureAwait(true);
        var merchant = tester.NewAccount();
        await merchant.GrantAccessAsync().WaitAsync(cancellation.Token).ConfigureAwait(true);
        await merchant.RegisterDerivationSchemeAsync(PayjoinConstants.BitcoinCode, ScriptPubKeyType.Segwit).WaitAsync(cancellation.Token).ConfigureAwait(true);
        var invoiceId = await CreateInvoiceIdAsync(merchant, cancellation.Token).ConfigureAwait(true);
        if (scenario == "plain-bitcoin-paid")
        {
            await merchant.PayInvoice(invoiceId).WaitAsync(cancellation.Token).ConfigureAwait(true);
        }
        var expectedFinal = scenario == "unresolved-final" ? ExpectedTransactionId : null;
        var bridges = tester.PayTester.GetService<IPayjoinAccountingBridgeService>();
        await bridges.CreateOrGetAsync(CreateRequest(invoiceId, merchant.StoreId, DateTimeOffset.UtcNow.AddHours(-7), expectedFinal), cancellation.Token).ConfigureAwait(true);
        await bridges.ExpirePendingAsync(DateTimeOffset.UtcNow, cancellation.Token).ConfigureAwait(true);
        await bridges.ExpirePendingAsync(DateTimeOffset.UtcNow, cancellation.Token).ConfigureAwait(true);
        var bridge = await bridges.TryGetByInvoiceIdAsync(invoiceId, cancellation.Token).ConfigureAwait(true);
        Assert.NotNull(bridge);
        Assert.Equal(PayjoinAccountingBridgeStatus.Expired, bridge.Status);
        Assert.Equal(expectedFinal, bridge.ExpectedFinalTransactionId);

        var entity = await tester.PayTester.GetService<InvoiceRepository>().GetInvoice(invoiceId).WaitAsync(cancellation.Token).ConfigureAwait(true);
        Assert.NotNull(entity);
        var payments = entity.GetPayments(false).ToArray();
        if (scenario == "plain-bitcoin-paid")
        {
            var payment = Assert.Single(payments);
            var handler = tester.PayTester.GetService<PaymentMethodHandlerDictionary>().GetBitcoinHandler(PayjoinConstants.BitcoinCode);
            Assert.Null(payment.GetDetails<BitcoinLikePaymentData>(handler).PayjoinInformation);
            Assert.Contains(entity.GetInvoiceState().Status, new[] { InvoiceStatus.Processing, InvoiceStatus.Settled });
        }
        else
        {
            Assert.Empty(payments);
            Assert.Equal(InvoiceStatus.New, entity.GetInvoiceState().Status);
        }

        var expiryMessage = expectedFinal is null
            ? PayjoinInvoiceEventPublisher.MessagePrefix + "Payjoin tracking expired."
            : PayjoinInvoiceEventPublisher.MessagePrefix + $"Payjoin settlement remains unresolved. Expected transaction: {expectedFinal}. Review the Async Payjoin page.";
        var logs = await WaitForJournalAsync(tester, invoiceId, log => log.Message == expiryMessage, cancellation.Token).ConfigureAwait(true);
        var expired = Assert.Single(logs, log => log.Message == expiryMessage);
        Assert.Equal(expectedFinal is null ? InvoiceEventData.EventSeverity.Info : InvoiceEventData.EventSeverity.Warning, expired.Severity);
        Assert.All(logs, log =>
        {
            Assert.DoesNotContain("was paid", log.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("did not complete", log.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Settled with", log.Message, StringComparison.OrdinalIgnoreCase);
        });

        var html = await RenderInvoiceAsync(tester, merchant, invoiceId, cancellation.Token).ConfigureAwait(true);
        Assert.Contains("Events", html, StringComparison.Ordinal);
        Assert.Contains(expiryMessage, html, StringComparison.Ordinal);
        Assert.DoesNotContain("the invoice was paid without it", html, StringComparison.Ordinal);
        var action = CreateActionContext(tester.PayTester.GetController<UIInvoiceController>(merchant.UserId, merchant.StoreId));
        Assert.False(tester.PayTester.GetService<ICompositeViewEngine>().FindView(action, "PayjoinInvoiceOutcome", isMainPage: false).Success);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task FailureAndAcceptedRetryAreLoggedOnceWhileRejectedRetriesAndPageReadsAddNothing()
    {
        using var cancellation = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        await tester.StartAsync().WaitAsync(cancellation.Token).ConfigureAwait(true);
        var merchant = tester.NewAccount();
        await merchant.GrantAccessAsync().WaitAsync(cancellation.Token).ConfigureAwait(true);
        await merchant.RegisterDerivationSchemeAsync(PayjoinConstants.BitcoinCode, ScriptPubKeyType.Segwit).WaitAsync(cancellation.Token).ConfigureAwait(true);
        var withoutPayjoinId = await CreateInvoiceIdAsync(merchant, cancellation.Token).ConfigureAwait(true);
        await RenderInvoiceAsync(tester, merchant, withoutPayjoinId, cancellation.Token).ConfigureAwait(true);
        Assert.Empty(await ReadJournalAsync(tester, withoutPayjoinId, cancellation.Token).ConfigureAwait(true));

        var invoiceId = await CreateInvoiceIdAsync(merchant, cancellation.Token).ConfigureAwait(true);
        var bridges = tester.PayTester.GetService<IPayjoinAccountingBridgeService>();
        await bridges.CreateOrGetAsync(CreateRequest(invoiceId, merchant.StoreId, DateTimeOffset.UtcNow.AddHours(1), ExpectedTransactionId), cancellation.Token).ConfigureAwait(true);
        await bridges.MarkFailedAsync(invoiceId, "controlled settlement mismatch", cancellation.Token).ConfigureAwait(true);
        await bridges.MarkFailedAsync(invoiceId, "controlled settlement mismatch", cancellation.Token).ConfigureAwait(true);
        Assert.Null(await bridges.TryRetryAsync(invoiceId, "wrong-store", DateTimeOffset.UtcNow, cancellation.Token).ConfigureAwait(true));
        Assert.NotNull(await bridges.TryRetryAsync(invoiceId, merchant.StoreId, DateTimeOffset.UtcNow, cancellation.Token).ConfigureAwait(true));
        Assert.Null(await bridges.TryRetryAsync(invoiceId, merchant.StoreId, DateTimeOffset.UtcNow, cancellation.Token).ConfigureAwait(true));
        var retryMessage = PayjoinInvoiceEventPublisher.MessagePrefix + "Payjoin settlement retry requested.";
        var logs = await WaitForJournalAsync(tester, invoiceId, log => log.Message == retryMessage, cancellation.Token).ConfigureAwait(true);
        Assert.Single(logs, log => log.Message.Contains("needs attention", StringComparison.Ordinal));
        Assert.Single(logs, log => log.Message == retryMessage);
        var bridge = await bridges.TryGetByInvoiceIdAsync(invoiceId, cancellation.Token).ConfigureAwait(true);
        Assert.NotNull(bridge);
        Assert.Equal(PayjoinAccountingBridgeStatus.PendingFinalTransaction, bridge.Status);
        Assert.Equal(ExpectedTransactionId, bridge.ExpectedFinalTransactionId);
        var entity = await tester.PayTester.GetService<InvoiceRepository>().GetInvoice(invoiceId).WaitAsync(cancellation.Token).ConfigureAwait(true);
        Assert.NotNull(entity);
        Assert.Empty(entity.GetPayments(false));

        await RenderInvoiceAsync(tester, merchant, invoiceId, cancellation.Token).ConfigureAwait(true);
        await RenderInvoiceAsync(tester, merchant, invoiceId, cancellation.Token).ConfigureAwait(true);
        Assert.Equal(logs.Select(log => log.Message), (await ReadJournalAsync(tester, invoiceId, cancellation.Token).ConfigureAwait(true)).Select(log => log.Message));
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task RealPayjoinSettlementHasOneProposalAndSuccessEntryWithTheAccountedTransaction()
    {
        using var cancellation = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cancellation.Token).ConfigureAwait(true);
        var payer = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(tester, context.Network, cancellationToken: cancellation.Token).ConfigureAwait(true);
        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cancellation.Token).ConfigureAwait(true);
        var invoice = await PayjoinInvoiceTestHelper.PreparePayjoinInvoiceAsync(tester, context.Merchant, context.Network, cancellation.Token).ConfigureAwait(true);
        var transactionId = await PayjoinIntegrationTestSupport.PayInvoiceViaExternalPayjoinPayerAsync(
            tester, payer, context.Network, context.Merchant.StoreId, invoice.PaymentUrl, cancellation.Token).ConfigureAwait(true);
        var result = await PayjoinInvoiceTestHelper.FinalizePayjoinPaymentAsync(tester, context.Merchant, invoice, transactionId, cancellation.Token).ConfigureAwait(true);
        PayjoinIntegrationTestSupport.AssertSuccessfulPayjoinTransaction(result);

        var logs = await PayjoinInvoiceTestHelper.AssertPayjoinJournalCompletedAsync(tester, invoice.InvoiceId, transactionId, cancellation.Token).ConfigureAwait(true);
        var success = Assert.Single(logs, log => log.Message.Contains("Settled with Payjoin", StringComparison.Ordinal));
        Assert.Equal(InvoiceEventData.EventSeverity.Success, success.Severity);
        var html = await RenderInvoiceAsync(tester, context.Merchant, invoice.InvoiceId, cancellation.Token).ConfigureAwait(true);
        Assert.Contains(success.Message, html, StringComparison.Ordinal);
        Assert.DoesNotContain("Settled with Async Payjoin", html, StringComparison.Ordinal);
    }

    private static async Task<string> CreateInvoiceIdAsync(TestAccount merchant, CancellationToken cancellationToken)
    {
        var invoice = await merchant.BitPay.CreateInvoiceAsync(new NBitpayClient.Invoice { Price = 0.001m, Currency = PayjoinConstants.BitcoinCode, FullNotifications = true }).WaitAsync(cancellationToken).ConfigureAwait(true);
        return invoice.Id;
    }

    private static CreatePayjoinAccountingBridgeRequest CreateRequest(string invoiceId, string storeId, DateTimeOffset expiresAt, string? expectedFinal = null) =>
        new(invoiceId, storeId, PayjoinConstants.BitcoinCode,
            PaymentTypes.CHAIN.GetPaymentMethodId(PayjoinConstants.BitcoinCode).ToString(), expiresAt,
            ExpectedFinalTransactionId: expectedFinal, ExpectedFinalOutputIndex: expectedFinal is null ? null : 1,
            ExpectedFinalValueSats: expectedFinal is null ? null : 100000);

    private static async Task<InvoiceEventData[]> ReadJournalAsync(ServerTester tester, string invoiceId, CancellationToken cancellationToken) =>
        (await tester.PayTester.GetService<InvoiceRepository>().GetInvoiceLogs(invoiceId).WaitAsync(cancellationToken).ConfigureAwait(true))
        .Where(log => log.Message.StartsWith(PayjoinInvoiceEventPublisher.MessagePrefix, StringComparison.Ordinal)).ToArray();

    private static async Task<InvoiceEventData[]> WaitForJournalAsync(ServerTester tester, string invoiceId, Func<InvoiceEventData, bool> expected, CancellationToken cancellationToken)
    {
        InvoiceEventData[] logs = [];
        await AsyncPolling.WaitUntilAsync(JournalTimeout, PollInterval,
            async token =>
            {
                logs = await ReadJournalAsync(tester, invoiceId, token).ConfigureAwait(true);
                return logs.Any(expected);
            }, shouldRetry: null,
            _ => $"Expected Payjoin event for invoice '{invoiceId}'. Messages: {string.Join("; ", logs.Select(log => log.Message))}",
            cancellationToken).ConfigureAwait(true);
        return logs;
    }

    private static ActionContext CreateActionContext(UIInvoiceController controller)
    {
        var http = controller.ControllerContext.HttpContext;
        http.Items[typeof(IUrlHelper)] = controller.Url;
        var routeData = new RouteData();
        routeData.Values["controller"] = "UIInvoice";
        routeData.Values["action"] = "Invoice";
        var descriptor = new ControllerActionDescriptor
        {
            ControllerName = "UIInvoice",
            ActionName = "Invoice",
            RouteValues = new Dictionary<string, string?> { ["controller"] = "UIInvoice", ["action"] = "Invoice" }
        };
        return new ActionContext(http, routeData, descriptor);
    }

    private static async Task<string> RenderInvoiceAsync(ServerTester tester, TestAccount merchant, string invoiceId, CancellationToken cancellationToken)
    {
        var controller = tester.PayTester.GetController<UIInvoiceController>(merchant.UserId, merchant.StoreId);
        var result = Assert.IsType<ViewResult>(await controller.Invoice(invoiceId).WaitAsync(cancellationToken).ConfigureAwait(true));
        Assert.IsType<InvoiceDetailsModel>(result.Model);
        var action = CreateActionContext(controller);
        var http = action.HttpContext;
        var found = tester.PayTester.GetService<ICompositeViewEngine>().GetView(null, "/Views/UIInvoice/Invoice.cshtml", isMainPage: true);
        Assert.True(found.Success, "BTCPay invoice view was not found.");
        var tempData = new TempDataDictionary(http, tester.PayTester.GetService<ITempDataProvider>());
        using var writer = new StringWriter();
        await found.View.RenderAsync(new ViewContext(action, found.View, result.ViewData, tempData, writer, new HtmlHelperOptions())).WaitAsync(cancellationToken).ConfigureAwait(true);
        return writer.ToString();
    }
}
