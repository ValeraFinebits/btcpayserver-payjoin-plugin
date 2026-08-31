using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;

namespace BTCPayServer.Plugins.Payjoin.Controllers;

/// <summary>How an action's unhandled exception is reported to the caller.</summary>
internal enum PayjoinErrorShape
{
    /// <summary>A status message on the next page, then a redirect back to where the operator was.</summary>
    Redirect,

    /// <summary>A JSON error body with status 500, for API and script callers.</summary>
    Json
}

/// <summary>
/// The last line of defence for every plugin controller. Core treats an unhandled exception in
/// a request whose stack includes a plugin assembly as a crashed plugin: its exception handler
/// disables the plugin on disk and stops the host three seconds later (only an attached debugger
/// suppresses this, which is why development never shows it). An MVC exception filter runs
/// before that handler, so an exception caught here never reaches it. The actions still handle
/// the failures they expect; this turns the ones they did not expect into an error the operator
/// can read instead of an outage.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
internal sealed class PayjoinExceptionFilterAttribute : ExceptionFilterAttribute
{
    private static readonly Action<ILogger, string, Exception?> LogUnhandledActionException =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(1, nameof(LogUnhandledActionException)),
            "Unhandled exception in payjoin action {Action}; reported to the caller instead of crashing the plugin");

    public PayjoinExceptionFilterAttribute(PayjoinErrorShape shape)
    {
        Shape = shape;
    }

    public PayjoinErrorShape Shape { get; }

    public override void OnException(ExceptionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ExceptionHandled)
        {
            return;
        }

        var logger = context.HttpContext.RequestServices.GetService<ILoggerFactory>()
            ?.CreateLogger<PayjoinExceptionFilterAttribute>();
        if (logger is not null)
        {
            LogUnhandledActionException(logger, context.ActionDescriptor.DisplayName ?? "unknown", context.Exception);
        }

        var message = $"Async payjoin could not complete the action: {context.Exception.Message}";
        context.Result = Shape == PayjoinErrorShape.Json
            ? new ObjectResult(new GreenfieldAPIError("internal-error", message)) { StatusCode = StatusCodes.Status500InternalServerError }
            : RedirectWithStatus(context, message);
        context.ExceptionHandled = true;
    }

    private static IActionResult RedirectWithStatus(ExceptionContext context, string message)
    {
        var tempDataFactory = context.HttpContext.RequestServices.GetService<ITempDataDictionaryFactory>();
        var tempData = tempDataFactory?.GetTempData(context.HttpContext);
        tempData?.SetStatusMessageModel(new StatusMessageModel
        {
            Severity = StatusMessageModel.StatusSeverity.Error,
            Message = message
        });

        // Back to the page the operator came from when it is one of ours; the plugin's overview
        // page otherwise. A redirect to another origin is never followed.
        var referer = context.HttpContext.Request.Headers.Referer.ToString();
        if (Uri.TryCreate(referer, UriKind.Absolute, out var refererUri) &&
            string.Equals(refererUri.Host, context.HttpContext.Request.Host.Host, StringComparison.OrdinalIgnoreCase))
        {
            return new LocalRedirectResult(refererUri.PathAndQuery);
        }

        return new RedirectToActionResult("Index", "UIPayjoinOverview", null);
    }
}
