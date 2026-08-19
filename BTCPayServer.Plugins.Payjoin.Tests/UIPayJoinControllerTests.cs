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

    private static async Task<PayjoinCheckoutAvailabilityResponse> GetCheckoutResponseAsync(GetBip21Response serviceResponse)
    {
        var paymentUrlService = Substitute.For<IPayjoinInvoicePaymentUrlService>();
        paymentUrlService.GetInvoicePaymentUrlAsync("invoice-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<GetBip21Response?>(serviceResponse));
        using var controller = new UIPayJoinController(null!, null!, null!, null!, null!, null!, paymentUrlService, null!);

        var result = await controller.GetInvoicePaymentUrl("invoice-1", TestContext.Current.CancellationToken).ConfigureAwait(true);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        return Assert.IsType<PayjoinCheckoutAvailabilityResponse>(okResult.Value);
    }

    [Theory]
    [InlineData(PayjoinAvailabilityStatus.TemporarilyUnavailable)]
    [InlineData(PayjoinAvailabilityStatus.MerchantRequirementsUnmet)]
    [InlineData(PayjoinAvailabilityStatus.DisabledByStore)]
    [InlineData(PayjoinAvailabilityStatus.InvoiceNotPayable)]
    public async Task GetInvoicePaymentUrlCollapsesEveryUnavailableStatus(PayjoinAvailabilityStatus status)
    {
        var response = await GetCheckoutResponseAsync(new GetBip21Response
        {
            Bip21 = "bitcoin:bcrt1qexample?amount=0.10000000",
            Status = status,
            UnavailableReason = "no confirmed receiver inputs are available",
            Retryable = false
        });

        Assert.Equal(PayjoinCheckoutAvailabilityStatus.Unavailable, response.Status);
    }

    [Theory]
    [InlineData(PayjoinAvailabilityStatus.TemporarilyUnavailable, true)]
    [InlineData(PayjoinAvailabilityStatus.TemporarilyUnavailable, false)]
    [InlineData(PayjoinAvailabilityStatus.MerchantRequirementsUnmet, false)]
    [InlineData(PayjoinAvailabilityStatus.DisabledByStore, false)]
    [InlineData(PayjoinAvailabilityStatus.InvoiceNotPayable, false)]
    public async Task GetInvoicePaymentUrlCarriesRetryableThrough(PayjoinAvailabilityStatus status, bool retryable)
    {
        var response = await GetCheckoutResponseAsync(new GetBip21Response
        {
            Bip21 = "bitcoin:bcrt1qexample?amount=0.10000000",
            Status = status,
            UnavailableReason = "no confirmed receiver inputs are available",
            Retryable = retryable
        });

        Assert.Equal(retryable, response.Retryable);
    }

    [Fact]
    public async Task GetInvoicePaymentUrlKeepsActiveStatus()
    {
        const string bip21 = "bitcoin:bcrt1qexample?amount=0.10000000&pjos=0&pj=https%3A%2F%2Fexample.com%2Fpj";

        var response = await GetCheckoutResponseAsync(new GetBip21Response
        {
            Bip21 = bip21,
            Status = PayjoinAvailabilityStatus.Active,
            Retryable = false
        });

        Assert.Equal(PayjoinCheckoutAvailabilityStatus.Active, response.Status);
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
    public async Task RunTestPaymentReturnsBadRequestWhenRequestIsNull()
    {
        using var controller = CreateController();

        var result = await controller.RunTestPayment(null!, TestContext.Current.CancellationToken);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var response = Assert.IsType<RunTestPaymentResponse>(badRequest.Value);
        Assert.False(response.Succeeded);
        Assert.Contains("invoiceId", response.Message, StringComparison.Ordinal);
        Assert.Null(response.TransactionId);
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
                Status = PayjoinAvailabilityStatus.Active,
                Retryable = false
            }));
        using var controller = new UIPayJoinController(null!, null!, null!, null!, null!, null!, paymentUrlService, null!);

        var result = await controller.RunTestPayment(new RunTestPaymentRequest
        {
            InvoiceId = "invoice-1"
        }, TestContext.Current.CancellationToken);

        AssertRunTestPaymentFailure(result, "invoice paymentUrl invalid");
    }

}
