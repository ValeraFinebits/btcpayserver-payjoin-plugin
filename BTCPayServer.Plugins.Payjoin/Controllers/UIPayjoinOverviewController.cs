using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin;

[Route("~/plugins/payjoin")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanViewProfile)]
public class UIPayjoinOverviewController : Controller
{
    private readonly IPayjoinStoreSettingsRepository _storeSettingsRepository;
    private readonly PayjoinAvailabilityService _availabilityService;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly NBXplorerDashboard _dashboard;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly IAuthorizationService _authorizationService;
    private readonly PayjoinBridgeAttentionService _bridgeAttentionService;
    private IStringLocalizer StringLocalizer { get; }

    private const string BitcoinCode = "BTC";

    public UIPayjoinOverviewController(
        IPayjoinStoreSettingsRepository storeSettingsRepository,
        PayjoinAvailabilityService availabilityService,
        PaymentMethodHandlerDictionary handlers,
        NBXplorerDashboard dashboard,
        BTCPayNetworkProvider networkProvider,
        IAuthorizationService authorizationService,
        PayjoinBridgeAttentionService bridgeAttentionService,
        IStringLocalizer stringLocalizer)
    {
        _storeSettingsRepository = storeSettingsRepository;
        _availabilityService = availabilityService;
        _handlers = handlers;
        _dashboard = dashboard;
        _networkProvider = networkProvider;
        _authorizationService = authorizationService;
        _bridgeAttentionService = bridgeAttentionService;
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
        var attentionBridges = await _bridgeAttentionService.GetRequiringAttentionAsync(currentStore.Id, HttpContext.RequestAborted).ConfigureAwait(false);
        var canRetryBridges = (await _authorizationService.AuthorizeAsync(User, currentStore.Id, Policies.CanModifyStoreSettings).ConfigureAwait(false)).Succeeded;
        ViewData.SetLayoutModel(new LayoutModel("PayjoinV2", "Async Payjoin"));
        return View(new PayjoinOverviewViewModel(currentStoreStatus, attentionBridges.Items, attentionBridges.TotalCount, canRetryBridges));
    }

    [HttpPost("bridges/{invoiceId}/retry")]
    public async Task<IActionResult> RetryBridge(string invoiceId)
    {
        var currentStore = HttpContext.GetNavStoreData();
        if (currentStore is null)
        {
            TempData[WellKnownTempData.ErrorMessage] = StringLocalizer["You need to select a store first."].Value;
            return RedirectToAction("Index", "UIHome");
        }

        var canModifyStoreSettings = (await _authorizationService.AuthorizeAsync(User, currentStore.Id, Policies.CanModifyStoreSettings).ConfigureAwait(false)).Succeeded;
        if (!canModifyStoreSettings)
        {
            return Forbid();
        }

        var retried = await _bridgeAttentionService.TryRetryAsync(invoiceId, currentStore.Id, HttpContext.RequestAborted).ConfigureAwait(false);
        if (!retried)
        {
            TempData[WellKnownTempData.ErrorMessage] = StringLocalizer["The settlement record could not be retried."].Value;
        }
        else
        {
            TempData[WellKnownTempData.SuccessMessage] = StringLocalizer["Settlement reconciliation will be retried for invoice {0}.", invoiceId].Value;
        }

        return RedirectToAction(nameof(Index));
    }

    private async Task<CurrentStorePayjoinStatusViewModel?> BuildCurrentStoreStatusAsync(StoreData? currentStore)
    {
        if (currentStore is null)
        {
            return null;
        }

        var settings = await _storeSettingsRepository.GetAsync(currentStore.Id).ConfigureAwait(false);
        var network = _networkProvider.GetNetwork<BTCPayNetwork>(BitcoinCode);
        var directoryUrls = settings.GetEffectiveDirectoryUrls();
        var ohttpRelayUrls = settings.GetEffectiveOhttpRelayUrls();

        var directoryConfigured = directoryUrls.Count > 0;
        var relayConfigured = ohttpRelayUrls.Count > 0;
        var hasColdWallet = !string.IsNullOrWhiteSpace(settings.ColdWalletDerivationScheme);
        var hasConfirmedReceiverInputs = network is not null &&
                                         await _availabilityService.HasConfirmedReceiverInputsAsync(currentStore.Id, BitcoinCode, network, HttpContext.RequestAborted).ConfigureAwait(false);

        var v1FallbackEffective = network is not null && IsPayjoinV1Effective(currentStore, network);
        var defaultCheckoutMode = ResolveDefaultCheckoutMode(settings.PayjoinV2Enabled, v1FallbackEffective);
        var fallbackTarget = ResolveFallbackTarget(settings.PayjoinV2Enabled, v1FallbackEffective);

        var status = ResolveStatus(directoryConfigured, relayConfigured, network is not null, hasConfirmedReceiverInputs, v1FallbackEffective);
        return new CurrentStorePayjoinStatusViewModel(
            currentStore.Id,
            currentStore.StoreName,
            directoryUrls,
            ohttpRelayUrls,
            hasColdWallet,
            hasConfirmedReceiverInputs,
            v1FallbackEffective,
            defaultCheckoutMode,
            fallbackTarget,
            status);
    }

    internal PayjoinCurrentStoreStatus ResolveStatus(bool directoryConfigured, bool relayConfigured, bool networkAvailable, bool hasConfirmedReceiverInputs, bool v1FallbackEffective)
    {
        if (!networkAvailable)
        {
            return new PayjoinCurrentStoreStatus(
                "danger",
                StringLocalizer["Unavailable"].Value,
                StringLocalizer["BTC network is not available on this server, so the basic Async Payjoin (Payjoin V2, BIP 77) prerequisites are not present for the selected store."].Value);
        }

        if (!directoryConfigured || !relayConfigured)
        {
            return new PayjoinCurrentStoreStatus(
                "danger",
                StringLocalizer["Needs configuration"].Value,
                StringLocalizer["The selected store is missing the directory URL or OHTTP relay URL required for the basic Async Payjoin (Payjoin V2, BIP 77) prerequisites."].Value);
        }

        if (!hasConfirmedReceiverInputs)
        {
            var pendingMessage = v1FallbackEffective
                ? StringLocalizer["Async Payjoin prerequisites are configured, but there are no confirmed receiver inputs right now, so checkout falls back to built-in Payjoin v1 (BIP 78)."].Value
                : StringLocalizer["Async Payjoin prerequisites are configured, but there are no confirmed receiver inputs right now, so checkout falls back to a standard Bitcoin payment."].Value;
            return new PayjoinCurrentStoreStatus(
                "warning",
                StringLocalizer["Additional requirements pending"].Value,
                pendingMessage);
        }

        var readyMessage = v1FallbackEffective
            ? StringLocalizer["Async Payjoin prerequisites are in place. Checkout may still fall back to built-in Payjoin v1 (BIP 78) if OHTTP dependencies are unavailable."].Value
            : StringLocalizer["Async Payjoin prerequisites are in place. Checkout may still fall back to a standard Bitcoin payment if OHTTP dependencies are unavailable."].Value;
        return new PayjoinCurrentStoreStatus(
            "success",
            StringLocalizer["Basic prerequisites present"].Value,
            readyMessage);
    }

    internal bool IsPayjoinV1Effective(StoreData store, BTCPayNetwork network)
    {
        var blob = store.GetStoreBlob();
        if (!blob.PayJoinEnabled || !network.SupportPayJoin)
        {
            return false;
        }

        var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(BitcoinCode);
        if (blob.IsExcluded(paymentMethodId))
        {
            return false;
        }

        var derivation = store.GetPaymentMethodConfig<DerivationSchemeSettings>(paymentMethodId, _handlers, false);
        if (derivation?.AccountDerivation is not { } accountDerivation)
        {
            return false;
        }

        var nodeSupportsTransactionCheck = _dashboard?.Get(network.CryptoCode)?.Status?.BitcoinStatus?.Capabilities?.CanSupportTransactionCheck is true;
        return IsPayjoinV1Effective(blob.PayJoinEnabled, network.SupportPayJoin, nodeSupportsTransactionCheck, derivation.IsHotWallet, accountDerivation.ScriptPubKeyType());
    }

    internal static bool IsPayjoinV1Effective(bool payJoinEnabled, bool networkSupportsPayJoin, bool nodeSupportsTransactionCheck, bool isHotWallet, NBitcoin.ScriptPubKeyType scriptType)
    {
        return payJoinEnabled
               && networkSupportsPayJoin
               && nodeSupportsTransactionCheck
               && isHotWallet
               && scriptType != NBitcoin.ScriptPubKeyType.Legacy;
    }

    internal static PayjoinCheckoutMode ResolveDefaultCheckoutMode(bool payjoinV2Default, bool v1FallbackEffective)
    {
        if (payjoinV2Default)
        {
            return PayjoinCheckoutMode.AsyncPayjoin;
        }

        return v1FallbackEffective ? PayjoinCheckoutMode.PayjoinV1 : PayjoinCheckoutMode.StandardBitcoin;
    }

    internal static PayjoinCheckoutMode? ResolveFallbackTarget(bool payjoinV2Default, bool v1FallbackEffective)
    {
        return ResolveDefaultCheckoutMode(payjoinV2Default, v1FallbackEffective) switch
        {
            PayjoinCheckoutMode.AsyncPayjoin => v1FallbackEffective ? PayjoinCheckoutMode.PayjoinV1 : PayjoinCheckoutMode.StandardBitcoin,
            PayjoinCheckoutMode.PayjoinV1 => PayjoinCheckoutMode.StandardBitcoin,
            _ => null
        };
    }
}

public class PayjoinOverviewViewModel
{
    public PayjoinOverviewViewModel(
        CurrentStorePayjoinStatusViewModel? currentStore,
        IReadOnlyCollection<PayjoinBridgeAttentionItem> attentionBridges,
        int attentionBridgesTotalCount,
        bool canRetryBridges)
    {
        CurrentStore = currentStore;
        AttentionBridges = attentionBridges;
        AttentionBridgesTotalCount = attentionBridgesTotalCount;
        CanRetryBridges = canRetryBridges;
    }

    public CurrentStorePayjoinStatusViewModel? CurrentStore { get; }

    public IReadOnlyCollection<PayjoinBridgeAttentionItem> AttentionBridges { get; }

    public int AttentionBridgesTotalCount { get; }

    public bool CanRetryBridges { get; }
}

public sealed class CurrentStorePayjoinStatusViewModel
{
    public CurrentStorePayjoinStatusViewModel(
        string storeId,
        string? storeName,
        IReadOnlyList<Uri> directoryUrls,
        IReadOnlyList<Uri> ohttpRelayUrls,
        bool hasColdWallet,
        bool hasConfirmedReceiverInputs,
        bool v1FallbackEffective,
        PayjoinCheckoutMode defaultCheckoutMode,
        PayjoinCheckoutMode? fallbackTarget,
        PayjoinCurrentStoreStatus status)
    {
        StoreId = storeId;
        StoreName = storeName;
        DirectoryUrls = directoryUrls;
        OhttpRelayUrls = ohttpRelayUrls;
        HasColdWallet = hasColdWallet;
        HasConfirmedReceiverInputs = hasConfirmedReceiverInputs;
        V1FallbackEffective = v1FallbackEffective;
        DefaultCheckoutMode = defaultCheckoutMode;
        FallbackTarget = fallbackTarget;
        Status = status;
    }

    public string StoreId { get; }

    public string? StoreName { get; }

    public IReadOnlyList<Uri> DirectoryUrls { get; }

    public IReadOnlyList<Uri> OhttpRelayUrls { get; }

    public bool HasColdWallet { get; }

    public bool HasConfirmedReceiverInputs { get; }

    public bool V1FallbackEffective { get; }

    public PayjoinCheckoutMode DefaultCheckoutMode { get; }

    public PayjoinCheckoutMode? FallbackTarget { get; }

    public PayjoinCurrentStoreStatus Status { get; }
}

public sealed record PayjoinCurrentStoreStatus(string Severity, string Title, string Message);

public enum PayjoinCheckoutMode
{
    AsyncPayjoin,
    PayjoinV1,
    StandardBitcoin
}
