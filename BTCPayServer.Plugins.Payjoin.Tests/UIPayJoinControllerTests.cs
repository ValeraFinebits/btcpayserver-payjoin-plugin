using BTCPayServer.Filters;
using BTCPayServer.Plugins.Payjoin.Controllers;
using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class UIPayJoinControllerTests
{
    private static UIPayJoinController CreateController()
    {
        return new UIPayJoinController(null!, null!, null!, null!, null!, null!, null!, null!);
    }

    private static void AssertRunTestPaymentFailure(ActionResult<RunTestPaymentResponse> actionResult, string expectedMessage)
    {
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var response = Assert.IsType<RunTestPaymentResponse>(okResult.Value);
        Assert.False(response.Succeeded);
        Assert.Equal(expectedMessage, response.Message);
        Assert.Null(response.TransactionId);
    }

    [Fact]
    public void RunTestPaymentUsesCheatModeRoute()
    {
        var method = typeof(UIPayJoinController).GetMethod(nameof(UIPayJoinController.RunTestPayment));

        Assert.NotNull(method);
        var attribute = Assert.Single(method.GetCustomAttributes(typeof(CheatModeRouteAttribute), inherit: true));
        Assert.IsType<CheatModeRouteAttribute>(attribute);
    }

    [Fact]
    public async Task RunTestPaymentThrowsWhenRequestIsNull()
    {
        using var controller = CreateController();

        await Assert.ThrowsAsync<ArgumentNullException>(() => controller.RunTestPayment(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunTestPaymentReturnsFailureWhenInvoiceIdMissing()
    {
        using var controller = CreateController();

        var result = await controller.RunTestPayment(new RunTestPaymentRequest(), TestContext.Current.CancellationToken);

        AssertRunTestPaymentFailure(result, "invoiceId is required");
    }

    [Fact]
    public async Task RunTestPaymentReturnsFailureWhenInvoicePaymentUrlUnavailable()
    {
        var paymentUrlService = Substitute.For<IPayjoinInvoicePaymentUrlService>();
        paymentUrlService.GetInvoicePaymentUrlAsync("invoice-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<GetBip21Response?>(null));
        using var controller = new UIPayJoinController(null!, null!, null!, null!, null!, null!, paymentUrlService, null!);

        var result = await controller.RunTestPayment(new RunTestPaymentRequest
        {
            InvoiceId = "invoice-1"
        }, TestContext.Current.CancellationToken);

        AssertRunTestPaymentFailure(result, "paymentUrl not available for invoice");
    }

    [Fact]
    public async Task RunTestPaymentReturnsFailureWhenInvoicePaymentUrlInvalid()
    {
        var paymentUrlService = Substitute.For<IPayjoinInvoicePaymentUrlService>();
        paymentUrlService.GetInvoicePaymentUrlAsync("invoice-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<GetBip21Response?>(new GetBip21Response
            {
                Bip21 = "not-a-valid-uri",
                PayjoinEnabled = true
            }));
        using var controller = new UIPayJoinController(null!, null!, null!, null!, null!, null!, paymentUrlService, null!);

        var result = await controller.RunTestPayment(new RunTestPaymentRequest
        {
            InvoiceId = "invoice-1"
        }, TestContext.Current.CancellationToken);

        AssertRunTestPaymentFailure(result, "invoice paymentUrl invalid");
    }

}
