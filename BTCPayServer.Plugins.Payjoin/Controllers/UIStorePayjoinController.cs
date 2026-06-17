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
        if (!ModelState.IsValid)
        {
            ViewData.SetLayoutModel(model.LayoutModel);
            return View("Settings", model);
        }

        string? validatedDerivationScheme = null;
        if (!string.IsNullOrWhiteSpace(model.ColdWalletDerivationScheme))
        {
            var network = _networkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode);
            if (network is null)
            {
                ModelState.AddModelError(nameof(model.ColdWalletDerivationScheme), "BTC network is not available.");
                ViewData.SetLayoutModel(model.LayoutModel);
                return View("Settings", model);
            }

            try
            {
                var parsed = DerivationSchemeHelper.Parse(model.ColdWalletDerivationScheme.Trim(), network);

                var wallet = _walletProvider.GetWallet(network);
                if (wallet is not null)
                {
                    await wallet.TrackAsync(parsed.AccountDerivation).ConfigureAwait(false);
                }

                validatedDerivationScheme = parsed.AccountDerivation.ToString();
            }
            catch (FormatException ex)
            {
                ModelState.AddModelError(nameof(model.ColdWalletDerivationScheme), $"Invalid wallet format: {ex.Message}");
                ViewData.SetLayoutModel(model.LayoutModel);
                return View("Settings", model);
            }
        }

        var settings = model.ToSettings(validatedDerivationScheme);

        await _settingsRepository.SetAsync(storeId, settings).ConfigureAwait(false);
        TempData[WellKnownTempData.SuccessMessage] = "Async Payjoin settings saved.";
        return RedirectToAction(nameof(Settings), new { storeId });
    }
}
