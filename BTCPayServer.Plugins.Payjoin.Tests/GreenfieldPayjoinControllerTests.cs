using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.Payjoin.Controllers;
using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Newtonsoft.Json.Linq;
using NSubstitute;
using System.Reflection;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class GreenfieldPayjoinControllerTests
{
    [Fact]
    public void ControllerUsesGreenfieldAuthentication()
    {
        var authorize = Assert.Single(typeof(GreenfieldPayjoinController).GetCustomAttributes<AuthorizeAttribute>());

        Assert.Equal(AuthenticationSchemes.Greenfield, authorize.AuthenticationSchemes);
    }

    [Fact]
    public void ControllerUsesGreenfieldApiConventions()
    {
        Assert.NotNull(typeof(GreenfieldPayjoinController).GetCustomAttribute<ApiControllerAttribute>());
        var cors = Assert.Single(typeof(GreenfieldPayjoinController).GetCustomAttributes<EnableCorsAttribute>());
        Assert.Equal(CorsPolicies.All, cors.PolicyName);
    }

    [Fact]
    public void SettingsEndpointsUseStoreSettingsPolicies()
    {
        AssertEndpoint(
            nameof(GreenfieldPayjoinController.GetSettings),
            "settings",
            Policies.CanViewStoreSettings,
            typeof(HttpGetAttribute));
        AssertEndpoint(
            nameof(GreenfieldPayjoinController.UpdateSettings),
            "settings",
            Policies.CanModifyStoreSettings,
            typeof(HttpPutAttribute));
    }

    [Fact]
    public void InvoicePaymentUrlEndpointUsesInvoiceViewPolicy()
    {
        AssertEndpoint(
            nameof(GreenfieldPayjoinController.GetInvoicePayjoinPaymentUrl),
            "~/api/v1/stores/{storeId}/invoices/{invoiceId}/payjoin/payment-url",
            Policies.CanViewInvoices,
            typeof(HttpGetAttribute));
    }

    [Fact]
    public async Task InvoicePaymentUrlEndpointReturnsPayjoinBip21Response()
    {
        const string storeId = "store-1";
        const string invoiceId = "invoice-1";
        const string bip21 = "bitcoin:bcrt1qexample?amount=0.10000000&pjos=0&pj=https%3A%2F%2Fexample.com%2Fpj";
        var invoiceLookup = Substitute.For<IPayjoinInvoiceLookup>();
        var paymentUrlService = Substitute.For<IPayjoinInvoicePaymentUrlService>();
        invoiceLookup.GetInvoiceAsync(invoiceId).Returns(Task.FromResult<InvoiceEntity?>(new InvoiceEntity
        {
            Id = invoiceId,
            StoreId = storeId
        }));
        paymentUrlService.GetInvoicePaymentUrlAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<GetBip21Response?>(new GetBip21Response
            {
                Bip21 = bip21,
                Status = PayjoinAvailabilityStatus.Active
            }));
        var controller = new GreenfieldPayjoinController(null!, paymentUrlService, invoiceLookup, null!, null!);

        var result = await controller.GetInvoicePayjoinPaymentUrl(storeId, invoiceId, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<GetBip21Response>(ok.Value);
        Assert.Equal(PayjoinAvailabilityStatus.Active, response.Status);
        Assert.Equal(bip21, response.Bip21);
        Assert.Contains("pjos=0", response.Bip21, StringComparison.Ordinal);
        Assert.Contains("pj=", response.Bip21, StringComparison.OrdinalIgnoreCase);
        await paymentUrlService.Received(1).GetInvoicePaymentUrlAsync(invoiceId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvoicePaymentUrlEndpointRejectsNonPayableInvoices()
    {
        const string storeId = "store-1";
        const string invoiceId = "invoice-1";
        var invoiceLookup = Substitute.For<IPayjoinInvoiceLookup>();
        var paymentUrlService = Substitute.For<IPayjoinInvoicePaymentUrlService>();
        invoiceLookup.GetInvoiceAsync(invoiceId).Returns(Task.FromResult<InvoiceEntity?>(new InvoiceEntity
        {
            Id = invoiceId,
            StoreId = storeId,
            Status = InvoiceStatus.Expired
        }));
        paymentUrlService.GetInvoicePaymentUrlAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<GetBip21Response?>(new GetBip21Response
            {
                Bip21 = "bitcoin:bcrt1qexample?amount=0.10000000&pjos=0&pj=https%3A%2F%2Fexample.com%2Fpj",
                Status = PayjoinAvailabilityStatus.Active
            }));
        var controller = new GreenfieldPayjoinController(null!, paymentUrlService, invoiceLookup, null!, null!);

        var result = await controller.GetInvoicePayjoinPaymentUrl(storeId, invoiceId, TestContext.Current.CancellationToken);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, objectResult.StatusCode);
        var error = Assert.IsType<GreenfieldAPIError>(objectResult.Value);
        Assert.Equal("payment-url-not-payable", error.Code);
        await paymentUrlService.DidNotReceive().GetInvoicePaymentUrlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateSettingsRequiresExplicitArrays()
    {
        var settingsRepository = Substitute.For<IPayjoinStoreSettingsRepository>();
        var controller = CreateController(settingsRepository);

        var result = await controller.UpdateSettings("store-1", new PayjoinStoreSettingsData());

        var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(result);
        var errors = Assert.IsAssignableFrom<List<GreenfieldValidationError>>(unprocessable.Value);
        Assert.Collection(
            errors.FindAll(error => error.Path == nameof(PayjoinStoreSettingsData.DirectoryUrls)),
            static error => Assert.Equal(nameof(PayjoinStoreSettingsData.DirectoryUrls), error.Path));
        Assert.Collection(
            errors.FindAll(error => error.Path == nameof(PayjoinStoreSettingsData.OhttpRelayUrls)),
            static error => Assert.Equal(nameof(PayjoinStoreSettingsData.OhttpRelayUrls), error.Path));
        await settingsRepository.DidNotReceive().SetAsync(Arg.Any<string>(), Arg.Any<PayjoinStoreSettings>());
    }

    [Fact]
    public async Task UpdateSettingsRejectsNullUrlEntries()
    {
        var settingsRepository = Substitute.For<IPayjoinStoreSettingsRepository>();
        var controller = CreateController(settingsRepository);
        var settings = JObject.Parse("""
            {
              "directoryUrls": [null, "https://configured.example/directory"],
              "ohttpRelayUrls": [null, "https://configured.example/relay"]
            }
            """).ToObject<PayjoinStoreSettingsData>()!;

        var result = await controller.UpdateSettings("store-1", settings);

        var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(result);
        var errors = Assert.IsAssignableFrom<List<GreenfieldValidationError>>(unprocessable.Value);
        Assert.Contains(errors, error => error.Path == nameof(PayjoinStoreSettingsData.DirectoryUrls));
        Assert.Contains(errors, error => error.Path == nameof(PayjoinStoreSettingsData.OhttpRelayUrls));
        await settingsRepository.DidNotReceive().SetAsync(Arg.Any<string>(), Arg.Any<PayjoinStoreSettings>());
    }

    [Fact]
    public async Task UpdateSettingsRejectsNonHttpsUrls()
    {
        var settingsRepository = Substitute.For<IPayjoinStoreSettingsRepository>();
        var controller = CreateController(settingsRepository);

        var result = await controller.UpdateSettings("store-1", new PayjoinStoreSettingsData
        {
            DirectoryUrls = [new Uri("http://fallback.example/directory")],
            OhttpRelayUrls = [new Uri("http://fallback.example/relay")]
        });

        var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(result);
        var errors = Assert.IsAssignableFrom<List<GreenfieldValidationError>>(unprocessable.Value);
        Assert.Contains(errors, error => error.Path == nameof(PayjoinStoreSettingsData.DirectoryUrls));
        Assert.Contains(errors, error => error.Path == nameof(PayjoinStoreSettingsData.OhttpRelayUrls));
        await settingsRepository.DidNotReceive().SetAsync(Arg.Any<string>(), Arg.Any<PayjoinStoreSettings>());
    }

    [Fact]
    public async Task UpdateSettingsUsesValidHttpsArrays()
    {
        var settingsRepository = Substitute.For<IPayjoinStoreSettingsRepository>();
        var controller = CreateController(settingsRepository);
        var expectedDirectoryUrls = new[] { new Uri("https://configured.example/directory") };
        var expectedRelayUrls = new[] { new Uri("https://configured.example/relay") };

        var result = await controller.UpdateSettings("store-1", new PayjoinStoreSettingsData
        {
            DirectoryUrls = expectedDirectoryUrls,
            OhttpRelayUrls = expectedRelayUrls
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<PayjoinStoreSettingsData>(ok.Value);
        Assert.Equal(expectedDirectoryUrls, response.DirectoryUrls);
        Assert.Equal(expectedRelayUrls, response.OhttpRelayUrls);
        await settingsRepository.Received(1).SetAsync(
            "store-1",
            Arg.Is<PayjoinStoreSettings>(saved =>
                saved.DirectoryUrls!.SequenceEqual(expectedDirectoryUrls) &&
                saved.OhttpRelayUrls!.SequenceEqual(expectedRelayUrls)));
    }

    private static GreenfieldPayjoinController CreateController(IPayjoinStoreSettingsRepository settingsRepository)
    {
        var controller = new GreenfieldPayjoinController(settingsRepository, null!, null!, null!, null!);
        var httpContext = new DefaultHttpContext();
        httpContext.SetStoreData(new BTCPayServer.Data.StoreData { Id = "store-1" });
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static void AssertEndpoint(string actionName, string routeTemplate, string policy, Type httpMethodAttributeType)
    {
        var method = typeof(GreenfieldPayjoinController).GetMethod(actionName)
            ?? throw new InvalidOperationException($"Missing action {actionName}");
        var authorize = Assert.Single(method.GetCustomAttributes<AuthorizeAttribute>());
        var httpMethod = Assert.Single(method.GetCustomAttributes(), attribute => attribute.GetType() == httpMethodAttributeType);
        var route = Assert.IsAssignableFrom<HttpMethodAttribute>(httpMethod);

        Assert.Equal(policy, authorize.Policy);
        Assert.Equal(AuthenticationSchemes.Greenfield, authorize.AuthenticationSchemes);
        Assert.Equal(routeTemplate, route.Template);
    }
}
