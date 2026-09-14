using AuthenticationSchemes = BTCPayServer.Abstractions.Constants.AuthenticationSchemes;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Payjoin.Controllers;
using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Plugins.Wallets;
using BTCPayServer.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

// Real Kestrel/MVC/auth middleware and production actions/filter. The test authentication
// handler supplies independent permissions; core's store-role database and Razor layout are
// outside this fixture. Returning view models as JSON keeps those dependencies out of the test.
public class PayjoinSenderHttpTests
{
    [Fact]
    public async Task RestrictedWalletRequestsUseRealActivationAndKeepOperationPermissionsSeparate()
    {
        using var database = new RelationalPluginTestContext();
        var store = database.CreateSenderStore();
        var app = CreateApp(store);
        await using var appLifetime = app.ConfigureAwait(false);
        await app.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, CheckCertificateRevocationList = true };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(app.Urls.Single()) };
        const string send = "/stores/store/payjoin/send";
        var create = WalletPolicies.CanCreateWalletTransactions;
        var sign = WalletPolicies.CanSignWalletTransactions;
        var broadcast = WalletPolicies.CanBroadcastWalletTransactions;
        var cancel = WalletPolicies.CanCancelWalletTransactions;
        var view = WalletPolicies.CanViewWallet;
        foreach (var permissions in new[] { create, $"{create},{sign}", $"{create},{broadcast}" })
        {
            using var denied = await PostAsync(client, send + "/from-wallet", permissions);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using (var allowed = await PostAsync(client, send + "/from-wallet", $"{create},{sign},{broadcast},{view}"))
        {
            Assert.Equal(HttpStatusCode.Redirect, allowed.StatusCode);
            Assert.Contains("wallets", allowed.Headers.Location!.OriginalString, StringComparison.Ordinal);
            using var wallet = await client.GetAsync(allowed.Headers.Location, TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, wallet.StatusCode);
            Assert.Empty(store.GetSessions("store")); // Invalid input bound through MVC, no payment built.
        }

        store.CreateSession("cancel", "store", "bitcoin:test", "test", 1000, "original", [],
            status: PayjoinSenderSessionStatus.AwaitingSignature, outpointsUsed: ["cancel:0"]);
        using (var deniedCancel = await PostAsync(client, send + "/cancel/cancel", create))
            Assert.Equal(HttpStatusCode.Forbidden, deniedCancel.StatusCode);
        using (var deniedBroadcast = await PostAsync(client, send + "/cancel/pay-now", cancel))
            Assert.Equal(HttpStatusCode.Forbidden, deniedBroadcast.StatusCode);
        using (var cancelled = await PostAsync(client, send + "/cancel/cancel", $"{cancel},{view}"))
        {
            Assert.Equal(HttpStatusCode.Redirect, cancelled.StatusCode);
            using var page = await client.GetAsync(cancelled.Headers.Location, TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Contains("cancelled", await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true), StringComparison.Ordinal);
        }
        Assert.Empty(store.GetOutpointsHeldByLiveSessions("store"));
        await app.StopAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    [Fact]
    public async Task UnexpectedErrorsStayGenericForAnonymousJsonAndUiRedirects()
    {
        using var database = new RelationalPluginTestContext();
        var app = CreateApp(database.CreateSenderStore());
        await using var appLifetime = app.ConfigureAwait(false);
        await app.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, CheckCertificateRevocationList = true };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(app.Urls.Single()) };
        using (var json = await client.GetAsync(new Uri("/tests/errors/json", UriKind.Relative), TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, json.StatusCode);
            Assert.DoesNotContain(HttpErrorController.Secret, await json.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true), StringComparison.Ordinal);
        }
        client.DefaultRequestHeaders.Add("X-Test-Permissions", WalletPolicies.CanViewWallet);
        client.DefaultRequestHeaders.Referrer = new Uri(client.BaseAddress, "/stores/store/payjoin/send");
        using var redirect = await client.GetAsync(new Uri("/tests/errors/ui", UriKind.Relative), TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        using var page = await client.GetAsync(redirect.Headers.Location, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var body = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Contains("could not complete", body, StringComparison.Ordinal);
        Assert.DoesNotContain(HttpErrorController.Secret, body, StringComparison.Ordinal);
        await app.StopAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string permissions)
    {
        client.DefaultRequestHeaders.Remove("X-Test-Permissions");
        client.DefaultRequestHeaders.Add("X-Test-Permissions", permissions);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["FeeSatoshiPerByte"] = "not-a-number" });
        return await client.PostAsync(new Uri(path, UriKind.Relative), content, TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    private static WebApplication CreateApp(PayjoinSenderSessionStore store)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        var mvc = builder.Services.AddControllersWithViews(options => options.Filters.Add(new ViewAsJson()));
        mvc.PartManager.ApplicationParts.Clear();
        mvc.AddApplicationPart(typeof(UIPayjoinSenderController).Assembly).AddApplicationPart(typeof(HttpErrorController).Assembly).AddControllersAsServices();
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton<IPayjoinSenderSessionProcessor>(PayjoinSenderSafetyTests.CreateProcessor(store));
        builder.Services.AddSingleton(new PayjoinSenderService(null!, null!, null!, null!, null!, store, null!, new PayjoinSessionBuildLock(), NullLogger<PayjoinSenderService>.Instance));
        builder.Services.AddSingleton(new WalletRepository(new ApplicationDbContextFactory(
            Options.Create(new DatabaseOptions { ConnectionString = "Host=unused;Database=unused" }), NullLoggerFactory.Instance)));
        builder.Services.AddAuthentication(AuthenticationSchemes.Cookie).AddScheme<AuthenticationSchemeOptions, PermissionAuthentication>(AuthenticationSchemes.Cookie, _ => { });
        builder.Services.AddAuthorization(options =>
        {
            foreach (var permission in new[] { WalletPolicies.CanCreateWalletTransactions, WalletPolicies.CanSignWalletTransactions,
                         WalletPolicies.CanBroadcastWalletTransactions, WalletPolicies.CanCancelWalletTransactions, WalletPolicies.CanViewWallet })
                options.AddPolicy(permission, policy => policy.RequireAuthenticatedUser().RequireClaim("permission", permission));
        });
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        return app;
    }

    private sealed class ViewAsJson : IResultFilter
    {
        public void OnResultExecuting(ResultExecutingContext context)
        {
            if (context.Result is ViewResult view && context.Controller is Controller controller)
                context.Result = new JsonResult(new { view.Model, Status = controller.TempData.GetStatusMessageModel() });
        }
        public void OnResultExecuted(ResultExecutedContext context) { }
    }

}

public sealed class PermissionAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var permissions = Request.Headers["X-Test-Permissions"].ToString();
        if (permissions.Length == 0)
            return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity(permissions.Split(',').Select(p => new Claim("permission", p)), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}

[Route("tests/errors")]
[AllowAnonymous]
public class HttpErrorController : Controller
{
    internal const string Secret = "SECRET-SENTINEL-DO-NOT-DISCLOSE";
    [HttpGet("json"), PayjoinExceptionFilter(PayjoinErrorShape.Json)]
    public IActionResult JsonError() => throw new InvalidOperationException(Secret + HttpContext.Request.Path);
    [HttpGet("ui"), PayjoinExceptionFilter(PayjoinErrorShape.Redirect)]
    public IActionResult UiError() => throw new InvalidOperationException(Secret + HttpContext.Request.Path);
}

[Area("Wallets")]
[Route("wallets/{walletId}/send")]
public class UIWalletsController : Controller
{
    [HttpGet]
    public IActionResult WalletSend() => Content("wallet");
}
