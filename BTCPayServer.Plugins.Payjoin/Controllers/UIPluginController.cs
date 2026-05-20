using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin;

[Route("~/plugins/template")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanViewProfile)]
public class UIPluginController : Controller
{
    private readonly PayjoinPluginService _pluginService;

    public UIPluginController(PayjoinPluginService pluginService)
    {
        _pluginService = pluginService;
    }

    // GET
    public async Task<IActionResult> Index()
    {
        var data = await _pluginService.Get().ConfigureAwait(false);
        ViewData.SetLayoutModel(new LayoutModel("Payjoin V2", "Payjoin V2").SetCategory(WellKnownCategories.Server));
        return View(new PluginPageViewModel(data));
    }
}

public class PluginPageViewModel
{
    public PluginPageViewModel(IReadOnlyCollection<PluginData> data)
    {
        Data = data;
    }

    public IReadOnlyCollection<PluginData> Data { get; }
}
