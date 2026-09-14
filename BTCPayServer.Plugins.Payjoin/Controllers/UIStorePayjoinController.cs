using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services.Wallets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Controllers;

[Route("~/stores/{storeId}/payjoin")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanViewStoreSettings)]
public class UIStorePayjoinController : Controller
{
    private readonly IPayjoinStoreSettingsRepository _settingsRepository;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly BTCPayWalletProvider _walletProvider;

    public UIStorePayjoinController(
        IPayjoinStoreSettingsRepository settingsRepository,
        BTCPayNetworkProvider networkProvider,
        BTCPayWalletProvider walletProvider)
    {
        _settingsRepository = settingsRepository;
        _networkProvider = networkProvider;
        _walletProvider = walletProvider;
    }

    [HttpGet("")]
    public async Task<IActionResult> Settings(string storeId)
    {
        var store = HttpContext.GetStoreData();
        if (store is null)
        {
            return NotFound();
        }

        var settings = await _settingsRepository.GetAsync(storeId).ConfigureAwait(false);
        if (settings is null)
        {
            TempData[WellKnownTempData.ErrorMessage] =
                "The saved Async Payjoin settings for this store could not be read, so payjoin is switched off for its checkouts. " +
                "The form below shows the defaults - review them and save to replace the damaged settings.";
            settings = new PayjoinStoreSettings();
        }

        var vm = PayjoinStoreSettingsViewModel.FromSettings(
            storeId,
            settings,
            new LayoutModel("Payjoin", "Async Payjoin Settings").SetCategory(WellKnownCategories.Store));
        ViewData.SetLayoutModel(vm.LayoutModel);
        return View(vm);
    }

    [HttpPost("")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> SettingsPost(string storeId, PayjoinStoreSettingsViewModel model)
    {
        if (model is null)
        {
            return BadRequest();
        }
        var store = HttpContext.GetStoreData();
        if (store is null)
        {
            return NotFound();
        }

        model.StoreId = storeId;
        model.LayoutModel = new LayoutModel("Payjoin", "Async Payjoin Settings").SetCategory(WellKnownCategories.Store);

        var directoryUrls = PayjoinStoreSettingsInput.ParseDirectoryUrlsTextWithErrors(model.DirectoryUrlsText ?? string.Empty, model.DirectoryUrls);
        var relayUrls = PayjoinStoreSettingsInput.ParseOhttpRelayUrlsTextWithErrors(model.OhttpRelayUrlsText ?? string.Empty, model.OhttpRelayUrls);

        foreach (var error in directoryUrls.Errors)
        {
            ModelState.AddModelError(nameof(model.DirectoryUrlsText), $"Line {error.LineNumber}: '{error.Value}' is invalid. {error.Message}");
        }

        foreach (var error in relayUrls.Errors)
        {
            ModelState.AddModelError(nameof(model.OhttpRelayUrlsText), $"Line {error.LineNumber}: '{error.Value}' is invalid. {error.Message}");
        }

        if (directoryUrls.Urls.Count == 0)
        {
            ModelState.AddModelError(nameof(model.DirectoryUrlsText), "At least one directory URL is required.");
        }

        if (relayUrls.Urls.Count == 0)
        {
            ModelState.AddModelError(nameof(model.OhttpRelayUrlsText), "At least one OHTTP relay URL is required.");
        }

        BTCPayNetwork? network = null;
        DerivationSchemeSettings? parsedColdWallet = null;
        if (!string.IsNullOrWhiteSpace(model.ColdWalletDerivationScheme))
        {
            network = _networkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode);
            if (network is null)
            {
                ModelState.AddModelError(nameof(model.ColdWalletDerivationScheme), "BTC network is not available.");
            }
            else
            {
                try
                {
                    parsedColdWallet = DerivationSchemeHelper.Parse(model.ColdWalletDerivationScheme.Trim(), network);
                }
                catch (FormatException ex)
                {
                    ModelState.AddModelError(nameof(model.ColdWalletDerivationScheme), $"Invalid wallet format: {ex.Message}");
                }
            }
        }

        if (!ModelState.IsValid)
        {
            ViewData.SetLayoutModel(model.LayoutModel);
            return View("Settings", model);
        }

        string? validatedDerivationScheme = null;
        if (parsedColdWallet is not null && network is not null)
        {
            var wallet = _walletProvider.GetWallet(network);
            if (wallet is not null)
            {
                await wallet.TrackAsync(parsedColdWallet.AccountDerivation).ConfigureAwait(false);
            }

            validatedDerivationScheme = parsedColdWallet.AccountDerivation.ToString();
        }

        var settings = model.ToSettings(validatedDerivationScheme);

        await _settingsRepository.SetAsync(storeId, settings).ConfigureAwait(false);
        TempData[WellKnownTempData.SuccessMessage] = "Async Payjoin settings saved.";
        return RedirectToAction(nameof(Settings), new { storeId });
    }
}
