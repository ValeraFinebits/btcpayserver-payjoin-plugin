using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services.Wallets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Controllers;

[ApiController]
[Route("~/api/v1/stores/{storeId}/payjoin")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
[EnableCors(CorsPolicies.All)]
public sealed class GreenfieldPayjoinController : ControllerBase
{
    private readonly IPayjoinStoreSettingsRepository _settingsRepository;
    private readonly IPayjoinInvoicePaymentUrlService _paymentUrlService;
    private readonly IPayjoinInvoiceLookup _invoiceLookup;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly BTCPayWalletProvider _walletProvider;

    public GreenfieldPayjoinController(
        IPayjoinStoreSettingsRepository settingsRepository,
        IPayjoinInvoicePaymentUrlService paymentUrlService,
        IPayjoinInvoiceLookup invoiceLookup,
        BTCPayNetworkProvider networkProvider,
        BTCPayWalletProvider walletProvider)
    {
        _settingsRepository = settingsRepository;
        _paymentUrlService = paymentUrlService;
        _invoiceLookup = invoiceLookup;
        _networkProvider = networkProvider;
        _walletProvider = walletProvider;
    }

    [HttpGet("settings")]
    [Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> GetSettings(string storeId)
    {
        if (HttpContext.GetStoreData() is null)
        {
            return this.CreateAPIError(404, "store-not-found", "The store was not found");
        }

        var settings = await _settingsRepository.GetAsync(storeId).ConfigureAwait(false);
        if (settings is null)
        {
            return this.CreateAPIError(503, "payjoin-settings-unreadable",
                "The store's payjoin settings could not be read. Replace them with a PUT to this endpoint.");
        }

        return Ok(PayjoinStoreSettingsData.FromSettings(settings));
    }

    [HttpPut("settings")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> UpdateSettings(string storeId, PayjoinStoreSettingsData settings)
    {
        if (HttpContext.GetStoreData() is null)
        {
            return this.CreateAPIError(404, "store-not-found", "The store was not found");
        }

        if (settings is null)
        {
            return this.CreateAPIError(400, "missing-request-body", "The request body is required");
        }

        var hasDirectoryUrls = settings.DirectoryUrls is not null;
        if (!hasDirectoryUrls)
        {
            ModelState.AddModelError(nameof(settings.DirectoryUrls), "The directoryUrls field is required.");
        }

        var hasRelayUrls = settings.OhttpRelayUrls is not null;
        if (!hasRelayUrls)
        {
            ModelState.AddModelError(nameof(settings.OhttpRelayUrls), "The ohttpRelayUrls field is required.");
        }

        foreach (var invalidDirectoryUrl in settings.GetInvalidDirectoryUrls())
        {
            var displayValue = invalidDirectoryUrl?.ToString() ?? "null";
            ModelState.AddModelError(nameof(settings.DirectoryUrls), $"'{displayValue}' is invalid. Only absolute HTTPS URLs are allowed.");
        }

        foreach (var invalidRelayUrl in settings.GetInvalidOhttpRelayUrls())
        {
            var displayValue = invalidRelayUrl?.ToString() ?? "null";
            ModelState.AddModelError(nameof(settings.OhttpRelayUrls), $"'{displayValue}' is invalid. Only absolute HTTPS URLs are allowed.");
        }

        if (hasDirectoryUrls && PayjoinStoreSettings.NormalizeDirectoryUrls(settings.DirectoryUrls).Count == 0)
        {
            ModelState.AddModelError(nameof(settings.DirectoryUrls), "At least one directory URL is required.");
        }

        if (hasRelayUrls && PayjoinStoreSettings.NormalizeOhttpRelayUrls(settings.OhttpRelayUrls).Count == 0)
        {
            ModelState.AddModelError(nameof(settings.OhttpRelayUrls), "At least one OHTTP relay URL is required.");
        }

        var validatedDerivationScheme = await ValidateColdWalletDerivationSchemeAsync(settings.ColdWalletDerivationScheme).ConfigureAwait(false);
        if (!ModelState.IsValid)
        {
            return this.CreateValidationError(ModelState);
        }

        var nextSettings = settings.ToSettings(validatedDerivationScheme);

        await _settingsRepository.SetAsync(storeId, nextSettings).ConfigureAwait(false);
        return Ok(PayjoinStoreSettingsData.FromSettings(nextSettings));
    }

    [HttpGet("~/api/v1/stores/{storeId}/invoices/{invoiceId}/payjoin/payment-url")]
    [Authorize(Policy = Policies.CanViewInvoices, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> GetInvoicePayjoinPaymentUrl(string storeId, string invoiceId, CancellationToken cancellationToken)
    {
        var invoice = await _invoiceLookup.GetInvoiceAsync(invoiceId).ConfigureAwait(false);
        if (invoice is null || !string.Equals(invoice.StoreId, storeId, StringComparison.Ordinal))
        {
            return this.CreateAPIError(404, "invoice-not-found", "The invoice was not found");
        }

        if (invoice.GetInvoiceState().Status != InvoiceStatus.New)
        {
            return this.CreateAPIError(404, "payment-url-not-payable", "The invoice is not payable");
        }

        var paymentUrl = await _paymentUrlService.GetInvoicePaymentUrlAsync(invoiceId, cancellationToken).ConfigureAwait(false);
        if (paymentUrl is null)
        {
            return this.CreateAPIError(404, "payment-url-not-found", "The Payjoin payment URL was not available");
        }

        return Ok(paymentUrl);
    }

    private async Task<string?> ValidateColdWalletDerivationSchemeAsync(string? coldWalletDerivationScheme)
    {
        if (string.IsNullOrWhiteSpace(coldWalletDerivationScheme))
        {
            return null;
        }

        var network = _networkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode);
        if (network is null)
        {
            ModelState.AddModelError(nameof(PayjoinStoreSettingsData.ColdWalletDerivationScheme), "BTC network is not available.");
            return null;
        }

        try
        {
            var parsed = DerivationSchemeHelper.Parse(coldWalletDerivationScheme.Trim(), network);
            var wallet = _walletProvider.GetWallet(network);
            if (wallet is not null)
            {
                // TODO: TrackAsync is not wallet recovery; establish verified cold-wallet ownership readiness before enabling Payjoin.
                await wallet.TrackAsync(parsed.AccountDerivation).ConfigureAwait(false);
            }

            return parsed.AccountDerivation.ToString();
        }
        catch (FormatException ex)
        {
            ModelState.AddModelError(nameof(PayjoinStoreSettingsData.ColdWalletDerivationScheme), $"Invalid wallet format: {ex.Message}");
            return null;
        }
    }
}
