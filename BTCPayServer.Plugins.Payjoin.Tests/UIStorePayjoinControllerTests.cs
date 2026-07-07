using BTCPayServer.Abstractions.Models;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Payjoin.Controllers;
using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using NSubstitute;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class UIStorePayjoinControllerTests
{
    [Fact]
    public async Task SettingsPostReturnsViewWhenTextUrlsAreInvalidEvenIfUriListsArePresent()
    {
        var settingsRepository = Substitute.For<IPayjoinStoreSettingsRepository>();
        using var controller = new UIStorePayjoinController(settingsRepository, null!, null!)
        {
            TempData = Substitute.For<ITempDataDictionary>()
        };
        InitializeController(controller);

        var result = await controller.SettingsPost("store-1", new PayjoinStoreSettingsViewModel
        {
            StoreId = "store-1",
            DirectoryUrls = [new Uri("https://fallback.example/directory")],
            DirectoryUrlsText = "not-a-directory-url",
            OhttpRelayUrls = [new Uri("https://fallback.example/relay")],
            OhttpRelayUrlsText = "not-a-relay-url",
            LayoutModel = new LayoutModel("Payjoin", "Async Payjoin Settings")
        });

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Settings", view.ViewName);
        var model = Assert.IsType<PayjoinStoreSettingsViewModel>(view.Model);
        Assert.Equal("not-a-directory-url", model.DirectoryUrlsText);
        Assert.Equal("not-a-relay-url", model.OhttpRelayUrlsText);
        Assert.False(controller.ModelState.IsValid);
        Assert.Contains(nameof(PayjoinStoreSettingsViewModel.DirectoryUrlsText), controller.ModelState.Keys);
        Assert.Contains(nameof(PayjoinStoreSettingsViewModel.OhttpRelayUrlsText), controller.ModelState.Keys);
        Assert.Contains(controller.ModelState[nameof(PayjoinStoreSettingsViewModel.DirectoryUrlsText)]!.Errors,
            error => error.ErrorMessage.Contains("Line 1", StringComparison.Ordinal) && error.ErrorMessage.Contains("Only absolute HTTPS URLs are allowed.", StringComparison.Ordinal));
        Assert.Contains(controller.ModelState[nameof(PayjoinStoreSettingsViewModel.OhttpRelayUrlsText)]!.Errors,
            error => error.ErrorMessage.Contains("Line 1", StringComparison.Ordinal) && error.ErrorMessage.Contains("Only absolute HTTPS URLs are allowed.", StringComparison.Ordinal));
        await settingsRepository.DidNotReceive().SetAsync(Arg.Any<string>(), Arg.Any<PayjoinStoreSettings>());
    }

    [Fact]
    public async Task SettingsPostRejectsNonHttpsUrlsWithDetailedErrors()
    {
        var settingsRepository = Substitute.For<IPayjoinStoreSettingsRepository>();
        using var controller = new UIStorePayjoinController(settingsRepository, null!, null!)
        {
            TempData = Substitute.For<ITempDataDictionary>()
        };
        InitializeController(controller);

        var result = await controller.SettingsPost("store-1", new PayjoinStoreSettingsViewModel
        {
            StoreId = "store-1",
            DirectoryUrlsText = "http://example.com/directory",
            OhttpRelayUrlsText = "http://example.com/relay",
            LayoutModel = new LayoutModel("Payjoin", "Async Payjoin Settings")
        });

        Assert.IsType<ViewResult>(result);
        Assert.Contains(controller.ModelState[nameof(PayjoinStoreSettingsViewModel.DirectoryUrlsText)]!.Errors,
            error => error.ErrorMessage.Contains("http://example.com/directory", StringComparison.Ordinal));
        Assert.Contains(controller.ModelState[nameof(PayjoinStoreSettingsViewModel.OhttpRelayUrlsText)]!.Errors,
            error => error.ErrorMessage.Contains("http://example.com/relay", StringComparison.Ordinal));
        await settingsRepository.DidNotReceive().SetAsync(Arg.Any<string>(), Arg.Any<PayjoinStoreSettings>());
    }

    [Fact]
    public async Task SettingsPostUsesActualLineNumbersWhenBlankLinesExist()
    {
        var settingsRepository = Substitute.For<IPayjoinStoreSettingsRepository>();
        using var controller = new UIStorePayjoinController(settingsRepository, null!, null!)
        {
            TempData = Substitute.For<ITempDataDictionary>()
        };
        InitializeController(controller);

        var result = await controller.SettingsPost("store-1", new PayjoinStoreSettingsViewModel
        {
            StoreId = "store-1",
            DirectoryUrlsText = "https://example.com/directory\n\nnot-a-directory-url",
            OhttpRelayUrlsText = "https://example.com/relay\n\nnot-a-relay-url",
            LayoutModel = new LayoutModel("Payjoin", "Async Payjoin Settings")
        });

        Assert.IsType<ViewResult>(result);
        Assert.Contains(controller.ModelState[nameof(PayjoinStoreSettingsViewModel.DirectoryUrlsText)]!.Errors,
            error => error.ErrorMessage.Contains("Line 3", StringComparison.Ordinal));
        Assert.Contains(controller.ModelState[nameof(PayjoinStoreSettingsViewModel.OhttpRelayUrlsText)]!.Errors,
            error => error.ErrorMessage.Contains("Line 3", StringComparison.Ordinal));
        await settingsRepository.DidNotReceive().SetAsync(Arg.Any<string>(), Arg.Any<PayjoinStoreSettings>());
    }

    [Fact]
    public async Task SettingsPostSavesValidTextUrlsWhenUriListsAreEmpty()
    {
        var settingsRepository = Substitute.For<IPayjoinStoreSettingsRepository>();
        using var controller = new UIStorePayjoinController(settingsRepository, null!, null!)
        {
            TempData = Substitute.For<ITempDataDictionary>()
        };
        InitializeController(controller);
        var expectedDirectoryUrls = new[] { new Uri("https://configured.example/directory") };
        var expectedRelayUrls = new[] { new Uri("https://configured.example/relay") };

        var result = await controller.SettingsPost("store-1", new PayjoinStoreSettingsViewModel
        {
            StoreId = "store-1",
            DirectoryUrls = [],
            DirectoryUrlsText = expectedDirectoryUrls[0].AbsoluteUri,
            OhttpRelayUrls = [],
            OhttpRelayUrlsText = expectedRelayUrls[0].AbsoluteUri,
            LayoutModel = new LayoutModel("Payjoin", "Async Payjoin Settings")
        });

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(UIStorePayjoinController.Settings), redirect.ActionName);
        await settingsRepository.Received(1).SetAsync(
            "store-1",
            Arg.Is<PayjoinStoreSettings>(saved =>
                saved.DirectoryUrls!.SequenceEqual(expectedDirectoryUrls) &&
                saved.OhttpRelayUrls!.SequenceEqual(expectedRelayUrls)));
    }

    private static void InitializeController(UIStorePayjoinController controller)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.SetStoreData(new StoreData { Id = "store-1" });
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }
}
