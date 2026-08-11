using BTCPayServer.Controllers;
using BTCPayServer.Models.InvoicingModels;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Tests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class PayjoinInvoiceOutcomeViewIntegrationTests : UnitTestBase
{
    private const string OutcomePartialName = "PayjoinInvoiceOutcome";
    private const string SettlementTransactionId = "0e33a4b1d1c0f2e6a5b4c3d2e1f0a9b8c7d6e5f4a3b2c1d0e9f8a7b6c5d4e3f2";

    public PayjoinInvoiceOutcomeViewIntegrationTests(ITestOutputHelper helper) : base(helper)
    {
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task InvoiceOutcomeReportsEveryBridgeStateAndStaysSilentWithoutABridge()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        await tester.StartAsync().WaitAsync(cts.Token).ConfigureAwait(true);

        var user = tester.NewAccount();
        await user.GrantAccessAsync().WaitAsync(cts.Token).ConfigureAwait(true);
        var bridges = tester.PayTester.GetService<IPayjoinAccountingBridgeService>();

        var settledInvoiceId = await CreateBridgeAsync(bridges, user.StoreId, cts.Token).ConfigureAwait(true);
        await bridges.MarkReconciledAsync(settledInvoiceId, SettlementTransactionId, 1, 1000, DateTimeOffset.UtcNow, cts.Token).ConfigureAwait(true);

        var inProgressInvoiceId = await CreateBridgeAsync(bridges, user.StoreId, cts.Token).ConfigureAwait(true);
        await bridges.SetExpectedFinalTransactionAsync(inProgressInvoiceId, SettlementTransactionId, 1, 1000, cts.Token).ConfigureAwait(true);

        var offeredInvoiceId = await CreateBridgeAsync(bridges, user.StoreId, cts.Token).ConfigureAwait(true);

        var failedInvoiceId = await CreateBridgeAsync(bridges, user.StoreId, cts.Token).ConfigureAwait(true);
        await bridges.MarkFailedAsync(failedInvoiceId, "settlement output could not be matched", cts.Token).ConfigureAwait(true);

        var expiredInvoiceId = await CreateBridgeAsync(bridges, user.StoreId, cts.Token, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-5)).ConfigureAwait(true);
        await bridges.ExpirePendingAsync(DateTimeOffset.UtcNow, cts.Token).ConfigureAwait(true);

        var settled = await RenderOutcomeAsync(tester, user, settledInvoiceId).ConfigureAwait(true);
        Assert.Contains("Settled with Async Payjoin", settled, StringComparison.Ordinal);
        Assert.Contains(SettlementTransactionId, settled, StringComparison.Ordinal);

        var inProgress = await RenderOutcomeAsync(tester, user, inProgressInvoiceId).ConfigureAwait(true);
        Assert.Contains("Async Payjoin in progress", inProgress, StringComparison.Ordinal);

        var offered = await RenderOutcomeAsync(tester, user, offeredInvoiceId).ConfigureAwait(true);
        Assert.Contains("Async Payjoin offered, not completed yet", offered, StringComparison.Ordinal);

        var expired = await RenderOutcomeAsync(tester, user, expiredInvoiceId).ConfigureAwait(true);
        Assert.Contains("Async Payjoin did not complete", expired, StringComparison.Ordinal);

        var failed = await RenderOutcomeAsync(tester, user, failedInvoiceId).ConfigureAwait(true);
        Assert.Contains("Async Payjoin settlement needs attention", failed, StringComparison.Ordinal);
        Assert.Contains("Review it on the Async Payjoin page", failed, StringComparison.Ordinal);
        Assert.DoesNotContain("RetryBridge", failed, StringComparison.OrdinalIgnoreCase);

        var failedForAnonymousViewer = await RenderOutcomeAsync(tester, user: null, failedInvoiceId).ConfigureAwait(true);
        Assert.Contains("Async Payjoin settlement needs attention", failedForAnonymousViewer, StringComparison.Ordinal);
        Assert.DoesNotContain("Review it on the Async Payjoin page", failedForAnonymousViewer, StringComparison.Ordinal);

        var withoutBridge = await RenderOutcomeAsync(tester, user, "invoice-without-bridge-" + Guid.NewGuid().ToString("N")).ConfigureAwait(true);
        Assert.True(string.IsNullOrWhiteSpace(withoutBridge), $"An invoice without an accounting bridge rendered '{withoutBridge}'.");
    }

    private static async Task<string> CreateBridgeAsync(
        IPayjoinAccountingBridgeService bridges,
        string storeId,
        CancellationToken cancellationToken,
        DateTimeOffset? expiresAt = null)
    {
        var invoiceId = "invoice-outcome-" + Guid.NewGuid().ToString("N");
        await bridges.CreateOrGetAsync(
            new CreatePayjoinAccountingBridgeRequest(
                InvoiceId: invoiceId,
                StoreId: storeId,
                CryptoCode: PayjoinConstants.BitcoinCode,
                PaymentMethodId: PaymentTypes.CHAIN.GetPaymentMethodId(PayjoinConstants.BitcoinCode).ToString(),
                ExpiresAt: expiresAt ?? DateTimeOffset.UtcNow.AddHours(1)),
            cancellationToken).ConfigureAwait(true);
        return invoiceId;
    }

    private static async Task<string> RenderOutcomeAsync(ServerTester tester, TestAccount? user, string invoiceId)
    {
        var controller = tester.PayTester.GetController<UIInvoiceController>(user?.UserId, user?.StoreId);
        var httpContext = controller.ControllerContext.HttpContext;
        httpContext.Items[typeof(IUrlHelper)] = controller.Url;
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());

        var viewEngine = tester.PayTester.GetService<ICompositeViewEngine>();
        var found = viewEngine.FindView(actionContext, OutcomePartialName, isMainPage: false);
        Assert.True(found.Success, $"BTCPay could not find the '{OutcomePartialName}' partial shipped by the plugin.");

        var viewData = new ViewDataDictionary<InvoiceDetailsModel>(new EmptyModelMetadataProvider(), new ModelStateDictionary())
        {
            Model = new InvoiceDetailsModel { Id = invoiceId }
        };
        var tempData = new TempDataDictionary(httpContext, tester.PayTester.GetService<ITempDataProvider>());
        using var writer = new StringWriter();
        var viewContext = new ViewContext(actionContext, found.View, viewData, tempData, writer, new HtmlHelperOptions());
        await found.View.RenderAsync(viewContext).ConfigureAwait(true);
        return writer.ToString();
    }
}
