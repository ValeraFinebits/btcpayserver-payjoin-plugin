using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.Payjoin.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

/// <summary>
/// Core treats an exception escaping a plugin action as a crashed plugin: it disables the plugin
/// and restarts the host. The filter must therefore handle everything and answer in the shape
/// the caller expects.
/// </summary>
public class PayjoinExceptionFilterTests
{
    [Fact]
    public void AJsonActionAnswersWithAFiveHundredBody()
    {
        var context = CreateContext(new InvalidOperationException("boom"));

        new PayjoinExceptionFilterAttribute(PayjoinErrorShape.Json).OnException(context);

        Assert.True(context.ExceptionHandled);
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, result.StatusCode);
        var error = Assert.IsType<GreenfieldAPIError>(result.Value);
        Assert.Equal("internal-error", error.Code);
        Assert.Contains("boom", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AUiActionRedirectsBackToTheReferringPage()
    {
        var context = CreateContext(new InvalidOperationException("boom"));
        context.HttpContext.Request.Host = new HostString("pay.example.test");
        context.HttpContext.Request.Headers.Referer = "https://pay.example.test/stores/abc/payjoin/send?x=1";

        new PayjoinExceptionFilterAttribute(PayjoinErrorShape.Redirect).OnException(context);

        Assert.True(context.ExceptionHandled);
        var redirect = Assert.IsType<LocalRedirectResult>(context.Result);
        Assert.Equal("/stores/abc/payjoin/send?x=1", redirect.Url);
    }

    [Fact]
    public void AUiActionWithAForeignRefererFallsBackToTheOverview()
    {
        // A redirect must never follow another origin.
        var context = CreateContext(new InvalidOperationException("boom"));
        context.HttpContext.Request.Host = new HostString("pay.example.test");
        context.HttpContext.Request.Headers.Referer = "https://evil.example/";

        new PayjoinExceptionFilterAttribute(PayjoinErrorShape.Redirect).OnException(context);

        var redirect = Assert.IsType<RedirectToActionResult>(context.Result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("UIPayjoinOverview", redirect.ControllerName);
    }

    [Fact]
    public void AnAlreadyHandledExceptionIsLeftAlone()
    {
        var context = CreateContext(new InvalidOperationException("boom"));
        context.ExceptionHandled = true;

        new PayjoinExceptionFilterAttribute(PayjoinErrorShape.Json).OnException(context);

        Assert.Null(context.Result);
    }

    private static ExceptionContext CreateContext(Exception exception)
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider()
        };
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ExceptionContext(actionContext, []) { Exception = exception };
    }
}
