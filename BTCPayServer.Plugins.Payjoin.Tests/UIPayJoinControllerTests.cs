using BTCPayServer.Client;
using BTCPayServer.Filters;
using BTCPayServer.Plugins.Payjoin.Controllers;
using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class UIPayJoinControllerTests
{
    private static UIPayJoinController CreateController(ILogger<UIPayJoinController>? logger = null)
    {
        return new UIPayJoinController(null!, null!, null!, null!, null!, null!, null!, null!, logger);
    }

    private static void AssertRunTestPaymentFailure(ActionResult<RunTestPaymentResponse> actionResult, string expectedMessage)
    {
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var response = Assert.IsType<RunTestPaymentResponse>(okResult.Value);
        Assert.False(response.Succeeded);
        Assert.Equal(expectedMessage, response.Message);
        Assert.Null(response.TransactionId);
    }

    private static async Task<GetCheckoutBip21Response> GetCheckoutResponseAsync(GetBip21Response serviceResponse)
    {
        var paymentUrlService = Substitute.For<IPayjoinInvoicePaymentUrlService>();
        paymentUrlService.GetInvoicePaymentUrlAsync("invoice-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<GetBip21Response?>(serviceResponse));
        using var controller = new UIPayJoinController(null!, null!, null!, null!, null!, null!, paymentUrlService, null!);

        var result = await controller.GetInvoicePaymentUrl("invoice-1", TestContext.Current.CancellationToken).ConfigureAwait(true);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        return Assert.IsType<GetCheckoutBip21Response>(okResult.Value);
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
            UnavailableReason = "no confirmed receiver inputs are available"
        });

        Assert.Equal(PayjoinCheckoutAvailabilityStatus.Unavailable, response.Status);
        Assert.Equal("bitcoin:bcrt1qexample?amount=0.10000000", response.Bip21);
    }

    [Fact]
    public async Task GetInvoicePaymentUrlKeepsActiveStatus()
    {
        const string bip21 = "bitcoin:bcrt1qexample?amount=0.10000000&pjos=0&pj=https%3A%2F%2Fexample.com%2Fpj";

        var response = await GetCheckoutResponseAsync(new GetBip21Response
        {
            Bip21 = bip21,
            Status = PayjoinAvailabilityStatus.Active
        });

        Assert.Equal(PayjoinCheckoutAvailabilityStatus.Active, response.Status);
        Assert.Equal(bip21, response.Bip21);
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
    public void SeedAttentionRecordUsesCheatModeRouteWithoutBypassingRequestProtections()
    {
        var method = typeof(UIPayJoinController).GetMethod(nameof(UIPayJoinController.SeedAttentionRecord));

        Assert.NotNull(method);
        var cheatModeAttribute = Assert.Single(method.GetCustomAttributes(typeof(CheatModeRouteAttribute), inherit: true));
        Assert.IsType<CheatModeRouteAttribute>(cheatModeAttribute);
        Assert.Empty(method.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true));
        Assert.Empty(method.GetCustomAttributes(typeof(IgnoreAntiforgeryTokenAttribute), inherit: true));
    }

    [Fact]
    public async Task SeedAttentionRecordForbidsCallerWithoutAccessToTheInvoiceStore()
    {
        const string invoiceId = "invoice-1";
        const string storeId = "store-1";
        var invoiceLookup = Substitute.For<IPayjoinInvoiceLookup>();
        invoiceLookup.GetInvoiceAsync(invoiceId).Returns(Task.FromResult<InvoiceEntity?>(new InvoiceEntity
        {
            Id = invoiceId,
            StoreId = storeId
        }));
        var authorizationService = Substitute.For<IAuthorizationService>();
        authorizationService
            .AuthorizeAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), storeId, Policies.CanModifyStoreSettings)
            .Returns(Task.FromResult(AuthorizationResult.Failed()));
        using var controller = CreateController();
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var result = await controller.SeedAttentionRecord(
            new SeedAttentionRecordRequest { InvoiceId = invoiceId },
            invoiceLookup,
            authorizationService,
            TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await authorizationService.Received(1)
            .AuthorizeAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), storeId, Policies.CanModifyStoreSettings);
    }

    [Theory]
    [InlineData(null, false, "Failed", "Failed")]
    [InlineData("expired", true, "Expired", "armed Expired")]
    public async Task SeedAttentionRecordCreatesTheRequestedFixture(
        string? requestedKind,
        bool expired,
        string expectedStatus,
        string expectedDescription)
    {
        const string invoiceId = "invoice-1";
        const string storeId = "store-1";
        var expectedKind = expired
            ? PayjoinAttentionRecordSeedKind.Expired
            : PayjoinAttentionRecordSeedKind.Failed;
        var seededStatus = expired
            ? PayjoinAccountingBridgeStatus.Expired
            : PayjoinAccountingBridgeStatus.Failed;
        var invoiceLookup = Substitute.For<IPayjoinInvoiceLookup>();
        invoiceLookup.GetInvoiceAsync(invoiceId).Returns(Task.FromResult<InvoiceEntity?>(new InvoiceEntity
        {
            Id = invoiceId,
            StoreId = storeId
        }));
        var authorizationService = Substitute.For<IAuthorizationService>();
        authorizationService
            .AuthorizeAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), storeId, Policies.CanModifyStoreSettings)
            .Returns(Task.FromResult(AuthorizationResult.Success()));
        var recordSeeder = new TestAttentionRecordSeeder(seededStatus);
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IPayjoinAttentionRecordSeeder)).Returns(recordSeeder);
        using var controller = CreateController();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { RequestServices = serviceProvider }
        };

        var result = await controller.SeedAttentionRecord(
            new SeedAttentionRecordRequest { InvoiceId = invoiceId, Kind = requestedKind },
            invoiceLookup,
            authorizationService,
            TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<SeedAttentionRecordResponse>(ok.Value);
        Assert.True(response.Succeeded);
        Assert.Equal(expectedStatus, response.Status);
        Assert.Contains(expectedDescription, response.Message, StringComparison.Ordinal);
        Assert.NotNull(recordSeeder.ReceivedRequest);
        Assert.Equal(invoiceId, recordSeeder.ReceivedRequest!.InvoiceId);
        Assert.Equal(storeId, recordSeeder.ReceivedRequest.StoreId);
        Assert.Equal(expectedKind, recordSeeder.ReceivedRequest.Kind);
    }

    [Fact]
    public async Task SeedAttentionRecordLogsUnexpectedFailureWithNonConflictingEventId()
    {
        const string invoiceId = "invoice-1";
        const string storeId = "store-1";
        var invoiceLookup = Substitute.For<IPayjoinInvoiceLookup>();
        invoiceLookup.GetInvoiceAsync(invoiceId).Returns(Task.FromResult<InvoiceEntity?>(new InvoiceEntity
        {
            Id = invoiceId,
            StoreId = storeId
        }));
        var authorizationService = Substitute.For<IAuthorizationService>();
        authorizationService
            .AuthorizeAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), storeId, Policies.CanModifyStoreSettings)
            .Returns(Task.FromResult(AuthorizationResult.Success()));
        var expectedException = new InvalidOperationException("Simulated seed failure.");
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider
            .GetService(typeof(IPayjoinAttentionRecordSeeder))
            .Returns(_ => throw expectedException);
        var logger = new TestLogger<UIPayJoinController>();
        using var controller = CreateController(logger);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { RequestServices = serviceProvider }
        };

        var result = await controller.SeedAttentionRecord(
            new SeedAttentionRecordRequest { InvoiceId = invoiceId },
            invoiceLookup,
            authorizationService,
            TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<SeedAttentionRecordResponse>(ok.Value);
        Assert.False(response.Succeeded);
        Assert.Contains(expectedException.Message, response.Message, StringComparison.Ordinal);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.LogLevel);
        Assert.Equal(new EventId(3, "LogSeedAttentionRecordFailed"), entry.EventId);
        Assert.Same(expectedException, entry.Exception);
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
                Status = PayjoinAvailabilityStatus.Active
            }));
        using var controller = new UIPayJoinController(null!, null!, null!, null!, null!, null!, paymentUrlService, null!);

        var result = await controller.RunTestPayment(new RunTestPaymentRequest
        {
            InvoiceId = "invoice-1"
        }, TestContext.Current.CancellationToken);

        AssertRunTestPaymentFailure(result, "invoice paymentUrl invalid");
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, eventId, exception));
        }

        public sealed record LogEntry(LogLevel LogLevel, EventId EventId, Exception? Exception);

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed class TestAttentionRecordSeeder(PayjoinAccountingBridgeStatus? result) : IPayjoinAttentionRecordSeeder
    {
        public SeedPayjoinAttentionRecordRequest? ReceivedRequest { get; private set; }

        public Task<PayjoinAccountingBridgeStatus?> TrySeedAttentionRecordAsync(
            SeedPayjoinAttentionRecordRequest request,
            CancellationToken cancellationToken)
        {
            ReceivedRequest = request;
            return Task.FromResult(result);
        }
    }

}
