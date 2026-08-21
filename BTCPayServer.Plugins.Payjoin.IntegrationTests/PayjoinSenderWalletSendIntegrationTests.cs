using BTCPayServer.Plugins.Payjoin.Controllers;
using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;
using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Plugins.Wallets.Views.ViewModels;
using BTCPayServer.Data;
using BTCPayServer.Services;
using BTCPayServer.Tests;
using BTCPayServer.Tests.Mocks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using NBitcoin.Payment;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests;

/// <summary>
/// The route an operator actually takes: BTCPay's own send screen posts its form to the plugin.
/// Every other test starts a session by calling the service, which is why a broken wallet link on
/// the plugin's own page survived a green suite.
/// </summary>
[Collection(nameof(NonParallelizableCollectionDefinition))]
public class PayjoinSenderWalletSendIntegrationTests : UnitTestBase
{
    public PayjoinSenderWalletSendIntegrationTests(ITestOutputHelper helper) : base(helper)
    {
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task TheSendScreenStartsASessionAndThePageLinksBackToTheWallet()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);
        var payer = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(tester, context.Network, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);
        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, payer.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var (invoiceId, bip21Response) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.Merchant, cts.Token).ConfigureAwait(true);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);

        // Core's send screen posts this: the URI it resolved, plus the amounts and fee rate the
        // operator sees on screen.
        var uriBuilder = new BitcoinUrlBuilder(bip21Response.Bip21, context.Network.NBitcoinNetwork);
        var sendModel = new WalletSendModel
        {
            // Empty on purpose: the extension clears core's field in the browser to disarm the
            // v1 flow and posts the URI under its own name. This is the shape a real submission
            // arrives in, and the shape an earlier version of this flow silently rejected.
            PayJoinBIP21 = null,
            FeeSatoshiPerByte = 5m,
            Outputs =
            [
                new WalletSendModel.TransactionOutput
                {
                    DestinationAddress = uriBuilder.Address!.ToString(),
                    Amount = uriBuilder.Amount!.ToDecimal(MoneyUnit.BTC),
                    Labels = ["payjoin-test"]
                }
            ]
        };

        using var controller = CreateController(tester);
        var posted = await controller.SendFromWallet(payer.StoreId, sendModel, bip21Response.Bip21, cts.Token).ConfigureAwait(true);

        // A redirect back to a wallet or plugin screen, not a re-render carrying an error.
        var redirect = Assert.IsType<RedirectToActionResult>(posted);
        Assert.NotEqual("WalletSend", redirect.ActionName);

        var senderSessionStore = tester.PayTester.GetService<PayjoinSenderSessionStore>();
        var session = Assert.Single(senderSessionStore.GetSessions(payer.StoreId));
        Assert.Equal(bip21Response.Bip21, session.Bip21);
        // The operator asked for 5 sat/vB, so that is the floor the receiver's proposal must clear.
        Assert.Equal(1250, session.FeeRateSatPerKwu);

        // The label the operator typed on the send screen is kept, the way core's own send keeps it.
        var walletId = new WalletId(payer.StoreId, PayjoinConstants.BitcoinCode);
        var walletRepository = tester.PayTester.GetService<WalletRepository>();
        var addressObject = await walletRepository
            .GetWalletObject(new WalletObjectId(walletId, WalletObjectData.Types.Address, uriBuilder.Address.ToString()))
            .ConfigureAwait(true);
        Assert.NotNull(addressObject);

        // The plugin's own page has to link back into the wallet. Core's binder rejects anything
        // that is not S-{storeId}-{code}, so a hand-built id would 404 every link on the page,
        // including the only route into the off-server signing flow.
        var listed = Assert.IsType<ViewResult>(controller.Send(payer.StoreId));
        var pageModel = Assert.IsType<PayjoinSenderViewModel>(listed.Model);
        Assert.True(WalletId.TryParse(pageModel.WalletId!, out var parsed));
        Assert.Equal(walletId, parsed);
        Assert.Single(pageModel.Sessions);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task ASecondDestinationIsRefusedBeforeAnythingIsBuilt()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);
        var payer = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(tester, context.Network, cancellationToken: cts.Token).ConfigureAwait(true);
        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, payer.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var sendModel = new WalletSendModel
        {
            PayJoinBIP21 = "bitcoin:tb1q?amount=0.001&pj=https://example.test/#K1",
            Outputs =
            [
                new WalletSendModel.TransactionOutput { DestinationAddress = "tb1qone", Amount = 0.001m },
                new WalletSendModel.TransactionOutput { DestinationAddress = "tb1qtwo", Amount = 0.002m }
            ]
        };

        using var controller = CreateController(tester);
        var posted = await controller.SendFromWallet(payer.StoreId, sendModel, asyncPayjoinBip21: null, cts.Token).ConfigureAwait(true);

        // Back to the send screen with the reason, and no session started.
        var redirect = Assert.IsType<RedirectToActionResult>(posted);
        Assert.Equal("WalletSend", redirect.ActionName);
        Assert.Empty(tester.PayTester.GetService<PayjoinSenderSessionStore>().GetSessions(payer.StoreId));
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task ABindingErrorRefusesTheSubmissionBeforeAnythingIsBuilt()
    {
        // A model the binder could not fully read must not start a session or create a signing
        // request, however usable the readable part looks.
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);
        var payer = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(tester, context.Network, cancellationToken: cts.Token).ConfigureAwait(true);
        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, payer.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var sendModel = new WalletSendModel
        {
            PayJoinBIP21 = "bitcoin:tb1qsomewhere?amount=0.001&pj=https://example.test/#K1",
            Outputs =
            [
                new WalletSendModel.TransactionOutput { DestinationAddress = "tb1qsomewhere", Amount = 0.001m }
            ]
        };

        using var controller = CreateController(tester);
        controller.ModelState.AddModelError(nameof(WalletSendModel.FeeSatoshiPerByte), "The value 'abc' is not valid.");
        var posted = await controller.SendFromWallet(payer.StoreId, sendModel, asyncPayjoinBip21: null, cts.Token).ConfigureAwait(true);

        var redirect = Assert.IsType<RedirectToActionResult>(posted);
        Assert.Equal("WalletSend", redirect.ActionName);
        Assert.Empty(tester.PayTester.GetService<PayjoinSenderSessionStore>().GetSessions(payer.StoreId));
        var pending = await tester.PayTester.GetService<BTCPayServer.HostedServices.PendingTransactionService>()
            .GetPendingTransactions(PayjoinConstants.BitcoinCode, payer.StoreId)
            .WaitAsync(cts.Token).ConfigureAwait(true);
        Assert.Empty(pending);
    }

    private static UIPayjoinSenderController CreateController(ServerTester tester)
    {
        var provider = tester.PayTester.ServiceProvider.GetRequiredService<IServiceScopeFactory>().CreateScope().ServiceProvider;
        var controller = new UIPayjoinSenderController(
            provider.GetRequiredService<PayjoinSenderService>(),
            provider.GetRequiredService<PayjoinSenderSessionStore>(),
            provider.GetRequiredService<IPayjoinSenderSessionProcessor>(),
            provider.GetRequiredService<WalletRepository>());

        var httpContext = new DefaultHttpContext { RequestServices = provider };
        httpContext.Request.Scheme = "http";
        httpContext.Request.Host = new HostString("127.0.0.1");
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        // The status messages this action sets go through the layout's URL helper, which needs an
        // action context the plugin never builds itself.
        controller.Url = new UrlHelperMock(new Uri("http://127.0.0.1/"));
        return controller;
    }
}
