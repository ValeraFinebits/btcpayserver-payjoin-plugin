using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using System;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin;

[Route("~/plugins/payjoin")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanViewProfile)]
public class UIPayjoinOverviewController : Controller
{
    private readonly IPayjoinStoreSettingsRepository _storeSettingsRepository;
    private readonly PayjoinAvailabilityService _availabilityService;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly IAuthorizationService _authorizationService;
    private IStringLocalizer StringLocalizer { get; }

    private const string BitcoinCode = "BTC";

    public UIPayjoinOverviewController(
        IPayjoinStoreSettingsRepository storeSettingsRepository,
        PayjoinAvailabilityService availabilityService,
        BTCPayNetworkProvider networkProvider,
        IAuthorizationService authorizationService,
        IStringLocalizer stringLocalizer)
    {
        _storeSettingsRepository = storeSettingsRepository;
        _availabilityService = availabilityService;
        _networkProvider = networkProvider;
        _authorizationService = authorizationService;
        StringLocalizer = stringLocalizer;
    }

    public async Task<IActionResult> Index()
    {
        var currentStore = HttpContext.GetNavStoreData();
        if (currentStore is null)
        {
            TempData[WellKnownTempData.ErrorMessage] = StringLocalizer["You need to select a store first."].Value;
            return RedirectToAction("Index", "UIHome");
        }

        var canViewStoreSettings = (await _authorizationService.AuthorizeAsync(User, currentStore.Id, Policies.CanViewStoreSettings).ConfigureAwait(false)).Succeeded;
        if (!canViewStoreSettings)
        {
            return Forbid();
        }

        var currentStoreStatus = await BuildCurrentStoreStatusAsync(currentStore).ConfigureAwait(false);
        ViewData.SetLayoutModel(new LayoutModel("PayjoinV2", "Payjoin V2").SetCategory(WellKnownCategories.Store));
        return View(new PayjoinOverviewViewModel(currentStoreStatus));
    }

    private async Task<CurrentStorePayjoinStatusViewModel?> BuildCurrentStoreStatusAsync(StoreData? currentStore)
    {
        if (currentStore is null)
        {
            return null;
        }

        var settings = await _storeSettingsRepository.GetAsync(currentStore.Id).ConfigureAwait(false);
        var network = _networkProvider.GetNetwork<BTCPayNetwork>(BitcoinCode);

        var directoryConfigured = settings.DirectoryUrl is not null;
        var relayConfigured = settings.OhttpRelayUrl is not null;
        var hasColdWallet = !string.IsNullOrWhiteSpace(settings.ColdWalletDerivationScheme);
        var hasConfirmedReceiverInputs = network is not null &&
                                         await _availabilityService.HasConfirmedReceiverInputsAsync(currentStore.Id, BitcoinCode, network, HttpContext.RequestAborted).ConfigureAwait(false);

        var status = ResolveStatus(directoryConfigured, relayConfigured, network is not null, hasConfirmedReceiverInputs);
        return new CurrentStorePayjoinStatusViewModel(
            currentStore.Id,
            currentStore.StoreName,
            settings.PayjoinV2Enabled,
            settings.DirectoryUrl,
            settings.OhttpRelayUrl,
            hasColdWallet,
            hasConfirmedReceiverInputs,
            status);
    }

    private PayjoinCurrentStoreStatus ResolveStatus(bool directoryConfigured, bool relayConfigured, bool networkAvailable, bool hasConfirmedReceiverInputs)
    {
        if (!networkAvailable)
        {
            return new PayjoinCurrentStoreStatus(
                "danger",
                StringLocalizer["Unavailable"].Value,
                StringLocalizer["BTC network is not available on this server, so the basic Payjoin V2 prerequisites are not present for the selected store."].Value);
        }

        if (!directoryConfigured || !relayConfigured)
        {
            return new PayjoinCurrentStoreStatus(
                "danger",
                StringLocalizer["Needs configuration"].Value,
                StringLocalizer["The selected store is missing the directory URL or OHTTP relay URL required for the basic Payjoin V2 prerequisites."].Value);
        }

        if (!hasConfirmedReceiverInputs)
        {
            return new PayjoinCurrentStoreStatus(
                "warning",
                StringLocalizer["Additional requirements pending"].Value,
                StringLocalizer["The basic Payjoin V2 prerequisites are configured, but the selected store has no confirmed receiver inputs right now, so checkout will fall back to a plain BIP21 payment URL."].Value);
        }

        return new PayjoinCurrentStoreStatus(
            "success",
            StringLocalizer["Basic prerequisites present"].Value,
            StringLocalizer["The selected store has the basic Payjoin V2 prerequisites in place. Checkout may still fall back to a plain BIP21 payment URL if external OHTTP dependencies are unavailable."].Value);
    }
}

public class PayjoinOverviewViewModel
{
    public PayjoinOverviewViewModel(CurrentStorePayjoinStatusViewModel? currentStore)
    {
        CurrentStore = currentStore;
    }

    public CurrentStorePayjoinStatusViewModel? CurrentStore { get; }
}

public sealed class CurrentStorePayjoinStatusViewModel
{
    public CurrentStorePayjoinStatusViewModel(
        string storeId,
        string? storeName,
        bool enabledByDefault,
        Uri? directoryUrl,
        Uri? ohttpRelayUrl,
        bool hasColdWallet,
        bool hasConfirmedReceiverInputs,
        PayjoinCurrentStoreStatus status)
    {
        StoreId = storeId;
        StoreName = storeName;
        EnabledByDefault = enabledByDefault;
        DirectoryUrl = directoryUrl;
        OhttpRelayUrl = ohttpRelayUrl;
        HasColdWallet = hasColdWallet;
        HasConfirmedReceiverInputs = hasConfirmedReceiverInputs;
        Status = status;
    }

    public string StoreId { get; }

    public string? StoreName { get; }

    public bool EnabledByDefault { get; }

    public Uri? DirectoryUrl { get; }

    public Uri? OhttpRelayUrl { get; }

    public bool HasColdWallet { get; }

    public bool HasConfirmedReceiverInputs { get; }

    public PayjoinCurrentStoreStatus Status { get; }
}

public sealed record PayjoinCurrentStoreStatus(string Severity, string Title, string Message);
