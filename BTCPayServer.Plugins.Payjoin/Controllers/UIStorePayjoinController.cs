using BTCPayServer;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Controllers;

[Route("~/stores/{storeId}/payjoin")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanViewStoreSettings)]
public class UIStorePayjoinController : Controller
{
    private const string PayjoinSettingsKey = "payjoin.settings";
    private readonly StoreRepository _storeRepository;

    public UIStorePayjoinController(StoreRepository storeRepository)
    {
        _storeRepository = storeRepository;
    }

    [HttpGet("")]
    public async Task<IActionResult> Settings(string storeId)
    {
        var store = HttpContext.GetStoreData() ?? await _storeRepository.FindStore(storeId).ConfigureAwait(false);
        if (store is null)
        {
            return NotFound();
        }

        var settings = await _storeRepository.GetSettingAsync<PayjoinStoreSettings>(storeId, PayjoinSettingsKey).ConfigureAwait(false)
            ?? new PayjoinStoreSettings();
        var vm = new PayjoinStoreSettingsViewModel
        {
            StoreId = storeId,
            EnabledByDefault = settings.EnabledByDefault,
            DirectoryUrl = settings.DirectoryUrl,
            OhttpRelayUrl = settings.OhttpRelayUrl,
            DemoMode = settings.DemoMode,
            LayoutModel = new LayoutModel("Payjoin", "Payjoin").SetCategory(WellKnownCategories.Store)
        };
        ViewData.SetLayoutModel(vm.LayoutModel);
        return View(vm);
    }

    [HttpPost("")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> SettingsPost(string storeId, PayjoinStoreSettingsViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var store = HttpContext.GetStoreData() ?? await _storeRepository.FindStore(storeId).ConfigureAwait(false);
        if (store is null)
        {
            return NotFound();
        }

        model.StoreId = storeId;
        model.LayoutModel = new LayoutModel("Payjoin", "Payjoin").SetCategory(WellKnownCategories.Store);
        if (!ModelState.IsValid)
        {
            ViewData.SetLayoutModel(model.LayoutModel);
            return View("Settings", model);
        }

        var settings = new PayjoinStoreSettings
        {
            EnabledByDefault = model.EnabledByDefault,
            DirectoryUrl = model.DirectoryUrl,
            OhttpRelayUrl = model.OhttpRelayUrl,
            DemoMode = model.DemoMode
        };

        await _storeRepository.UpdateSetting(storeId, PayjoinSettingsKey, settings).ConfigureAwait(false);
        TempData[WellKnownTempData.SuccessMessage] = "Payjoin settings saved.";
        return RedirectToAction(nameof(Settings), new { storeId });
    }
}
