using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Payjoin.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;

namespace BTCPayServer.Plugins.Payjoin.Controllers;

[Route("~/stores/{storeId}/payjoin")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanViewStoreSettings)]
public class UIStorePayjoinController : Controller
{
    [HttpGet("")]
    public IActionResult Settings(string storeId)
    {
        var vm = new PayjoinStoreSettingsViewModel
        {
            StoreId = storeId,
            LayoutModel = new LayoutModel("Payjoin", "Payjoin").SetCategory(WellKnownCategories.Store)
        };
        ViewData.SetLayoutModel(vm.LayoutModel);
        return View(vm);
    }

    [HttpPost("")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
    public IActionResult SettingsPost(string storeId, PayjoinStoreSettingsViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        model.StoreId = storeId;
        model.LayoutModel = new LayoutModel("Payjoin", "Payjoin").SetCategory(WellKnownCategories.Store);
        ViewData.SetLayoutModel(model.LayoutModel);
        return View("Settings", model);
    }
}
