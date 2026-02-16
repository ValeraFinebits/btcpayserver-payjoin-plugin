using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Plugins.Payjoin.Services;
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
    private readonly PayjoinDemoContext _demoContext;

    public UIStorePayjoinController(StoreRepository storeRepository, PayjoinDemoContext demoContext)
    {
        _storeRepository = storeRepository;
        _demoContext = demoContext;
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
        if (model is null)
        {
            return BadRequest();
        }
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

        if (settings.DemoMode)
        {
            _demoContext.Initialize();
            if (_demoContext.DirectoryUrl is not null)
            {
                settings.DirectoryUrl = _demoContext.DirectoryUrl;
            }

            if (_demoContext.OhttpRelayUrl is not null)
            {
                settings.OhttpRelayUrl = _demoContext.OhttpRelayUrl;
            }
        }
        else
        {
            settings.DirectoryUrl = null;
            settings.OhttpRelayUrl = null;
        }

        await _storeRepository.UpdateSetting(storeId, PayjoinSettingsKey, settings).ConfigureAwait(false);
        TempData[WellKnownTempData.SuccessMessage] = "Payjoin settings saved.";
        return RedirectToAction(nameof(Settings), new { storeId });
    }

    [HttpPost("demo-mode")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
    public async Task<IActionResult> ToggleDemoMode(string storeId, [FromForm] bool demoMode)
    {
        var store = HttpContext.GetStoreData() ?? await _storeRepository.FindStore(storeId).ConfigureAwait(false);
        if (store is null)
        {
            return NotFound();
        }

        var settings = await _storeRepository.GetSettingAsync<PayjoinStoreSettings>(storeId, PayjoinSettingsKey).ConfigureAwait(false)
            ?? new PayjoinStoreSettings();
        settings.DemoMode = demoMode;
        if (settings.DemoMode)
        {
            _demoContext.Initialize();
            if (_demoContext.DirectoryUrl is not null)
            {
                settings.DirectoryUrl = _demoContext.DirectoryUrl;
            }

            if (_demoContext.OhttpRelayUrl is not null)
            {
                settings.OhttpRelayUrl = _demoContext.OhttpRelayUrl;
            }
        }
        else
        {
            settings.DirectoryUrl = null;
            settings.OhttpRelayUrl = null;
        }

        await _storeRepository.UpdateSetting(storeId, PayjoinSettingsKey, settings).ConfigureAwait(false);
        return Json(new
        {
            directoryUrl = settings.DirectoryUrl?.ToString(),
            ohttpRelayUrl = settings.OhttpRelayUrl?.ToString()
        });
    }
}
