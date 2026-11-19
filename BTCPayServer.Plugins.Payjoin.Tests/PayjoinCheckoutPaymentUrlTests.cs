using BTCPayServer.Plugins.Payjoin.Controllers;
using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using NicolasDorier.RateLimits;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class PayjoinCheckoutPaymentUrlTests
{
    [Fact]
    public async Task EquivalentCheckoutRequestsShareGenerationAndCancelIndependently()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var service = Substitute.For<IPayjoinInvoicePaymentUrlService>();
        var generated = new TaskCompletionSource<GetBip21Response?>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.GetInvoicePaymentUrlAsync("invoice", Arg.Any<CancellationToken>()).Returns(generated.Task);
        using var checkout = new PayjoinCheckoutPaymentUrl(service, Substitute.For<IHostApplicationLifetime>());

        var canceled = Assert.IsAssignableFrom<Task<GetBip21Response?>>(checkout.TryGetPaymentUrl("invoice", caller.Token));
        var shared = Assert.IsAssignableFrom<Task<GetBip21Response?>>(checkout.TryGetPaymentUrl("invoice", cancellationToken));
        await caller.CancelAsync().ConfigureAwait(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled).ConfigureAwait(true);
        Assert.False(shared.IsCompleted);

        var fallback = new GetBip21Response
        {
            Bip21 = "bitcoin:example",
            Status = PayjoinAvailabilityStatus.TemporarilyUnavailable,
            Retryable = true
        };
        generated.SetResult(fallback);
        Assert.Same(fallback, await shared.ConfigureAwait(true));
        await service.Received(1).GetInvoicePaymentUrlAsync("invoice", CancellationToken.None).ConfigureAwait(true);

        var retry = Assert.IsAssignableFrom<Task<GetBip21Response?>>(checkout.TryGetPaymentUrl("invoice", cancellationToken));
        Assert.Same(fallback, await retry.ConfigureAwait(true));
        await service.Received(2).GetInvoicePaymentUrlAsync("invoice", CancellationToken.None).ConfigureAwait(true);
    }

    [Fact]
    public async Task CheckoutReturnsRetryableHttpStatusWhenDistinctInvoiceWorkIsFull()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var service = Substitute.For<IPayjoinInvoicePaymentUrlService>();
        var generated = new TaskCompletionSource<GetBip21Response?>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.GetInvoicePaymentUrlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(generated.Task);
        using var checkout = new PayjoinCheckoutPaymentUrl(service, Substitute.For<IHostApplicationLifetime>());
        var active = Enumerable.Range(0, PayjoinCheckoutPaymentUrl.MaxConcurrentInvoices)
            .Select(i => Assert.IsAssignableFrom<Task<GetBip21Response?>>(checkout.TryGetPaymentUrl($"invoice-{i}", cancellationToken)))
            .ToArray();

        using var controller = new UIPayJoinController(null!, null!, null!, null!, null!, null!, service, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var refused = await controller.GetInvoicePaymentUrl("another-invoice", checkout, cancellationToken).ConfigureAwait(true);
        Assert.Equal(StatusCodes.Status429TooManyRequests, Assert.IsType<StatusCodeResult>(refused.Result).StatusCode);
        Assert.Equal("1", controller.Response.Headers.RetryAfter.ToString());
        await service.DidNotReceive().GetInvoicePaymentUrlAsync("another-invoice", Arg.Any<CancellationToken>()).ConfigureAwait(true);

        generated.SetResult(null);
        await Task.WhenAll(active).ConfigureAwait(true);
        var retry = await controller.GetInvoicePaymentUrl("another-invoice", checkout, cancellationToken).ConfigureAwait(true);
        Assert.IsType<NotFoundResult>(retry.Result);
    }

    [Fact]
    public void OnlyCheckoutPaymentUrlActionHasItsRemoteAddressLimit()
    {
        var checkout = typeof(UIPayJoinController).GetMethod(nameof(UIPayJoinController.GetInvoicePaymentUrl))!;
        var greenfield = typeof(GreenfieldPayjoinController).GetMethod(nameof(GreenfieldPayjoinController.GetInvoicePayjoinPaymentUrl))!;

        var filter = Assert.IsType<RateLimitsFilterAttribute>(
            Assert.Single(checkout.GetCustomAttributes(typeof(RateLimitsFilterAttribute), inherit: false)));
        Assert.Equal(RateLimitsScope.RemoteAddress, filter.Scope);
        Assert.Empty(greenfield.GetCustomAttributes(typeof(RateLimitsFilterAttribute), inherit: false));
    }
}
