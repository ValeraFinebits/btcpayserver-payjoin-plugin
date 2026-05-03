using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.Payjoin.Controllers;
using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
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
                PayjoinEnabled = true
            }));
        var controller = new GreenfieldPayjoinController(null!, paymentUrlService, invoiceLookup, null!, null!);

        var result = await controller.GetInvoicePayjoinPaymentUrl(storeId, invoiceId, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<GetBip21Response>(ok.Value);
        Assert.True(response.PayjoinEnabled);
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
                PayjoinEnabled = true
            }));
        var controller = new GreenfieldPayjoinController(null!, paymentUrlService, invoiceLookup, null!, null!);

        var result = await controller.GetInvoicePayjoinPaymentUrl(storeId, invoiceId, TestContext.Current.CancellationToken);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, objectResult.StatusCode);
        var error = Assert.IsType<GreenfieldAPIError>(objectResult.Value);
        Assert.Equal("payment-url-not-payable", error.Code);
        await paymentUrlService.DidNotReceive().GetInvoicePaymentUrlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
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
