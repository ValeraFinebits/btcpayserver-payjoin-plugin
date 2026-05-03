using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Payments;
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
        return Ok(ToData(settings));
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

        if (settings.DirectoryUrl is null)
        {
            ModelState.AddModelError(nameof(settings.DirectoryUrl), "DirectoryUrl is required.");
        }

        if (settings.OhttpRelayUrl is null)
        {
            ModelState.AddModelError(nameof(settings.OhttpRelayUrl), "OhttpRelayUrl is required.");
        }

        var validatedDerivationScheme = await ValidateColdWalletDerivationSchemeAsync(settings.ColdWalletDerivationScheme).ConfigureAwait(false);
        if (!ModelState.IsValid)
        {
            return this.CreateValidationError(ModelState);
        }

        var nextSettings = new PayjoinStoreSettings
        {
            EnabledByDefault = settings.EnabledByDefault,
            DirectoryUrl = settings.DirectoryUrl,
            OhttpRelayUrl = settings.OhttpRelayUrl,
            ColdWalletDerivationScheme = validatedDerivationScheme
        };

        await _settingsRepository.SetAsync(storeId, nextSettings).ConfigureAwait(false);
        return Ok(ToData(nextSettings));
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

        var network = _networkProvider.GetNetwork<BTCPayNetwork>("BTC");
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

    private static PayjoinStoreSettingsData ToData(PayjoinStoreSettings settings)
    {
        return new PayjoinStoreSettingsData
        {
            EnabledByDefault = settings.EnabledByDefault,
            DirectoryUrl = settings.DirectoryUrl,
            OhttpRelayUrl = settings.OhttpRelayUrl,
            ColdWalletDerivationScheme = settings.ColdWalletDerivationScheme
        };
    }
}
