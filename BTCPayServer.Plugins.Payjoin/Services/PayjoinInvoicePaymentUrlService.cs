using BTCPayServer.Client.Models;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Logging;
using Payjoin;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using PayjoinUri = Payjoin.Uri;

namespace BTCPayServer.Plugins.Payjoin.Services;

public sealed class PayjoinInvoicePaymentUrlService : IPayjoinInvoicePaymentUrlService
{
    private static readonly Action<ILogger, string, Exception?> LogPayjoinPaymentUrlFallback =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(1, nameof(LogPayjoinPaymentUrlFallback)),
            "Payjoin payment URL generation failed for {InvoiceId}. Falling back to plain BIP21.");

    private readonly InvoiceRepository _invoiceRepository;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly IPayjoinStoreSettingsRepository _storeSettingsRepository;
    private readonly PayjoinUriSessionService _payjoinUriSessionService;
    private readonly ILogger<PayjoinInvoicePaymentUrlService>? _logger;

    public PayjoinInvoicePaymentUrlService(
        InvoiceRepository invoiceRepository,
        BTCPayNetworkProvider networkProvider,
        IPayjoinStoreSettingsRepository storeSettingsRepository,
        PayjoinUriSessionService payjoinUriSessionService,
        ILogger<PayjoinInvoicePaymentUrlService>? logger = null)
    {
        _invoiceRepository = invoiceRepository;
        _networkProvider = networkProvider;
        _storeSettingsRepository = storeSettingsRepository;
        _payjoinUriSessionService = payjoinUriSessionService;
        _logger = logger;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Integration fallback belongs at the public service boundary and must return plain BIP21 instead of escaping unexpected payjoin errors.")]
    public async Task<GetBip21Response?> GetInvoicePaymentUrlAsync(string invoiceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(invoiceId))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(invoiceId));
        }

        var invoice = await _invoiceRepository.GetInvoice(invoiceId).ConfigureAwait(false);
        if (invoice is null)
        {
            return null;
        }

        if (invoice.GetInvoiceState().Status != InvoiceStatus.New)
        {
            return null;
        }

        var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(PayjoinConstants.BitcoinCode);
        var prompt = invoice.GetPaymentPrompt(paymentMethodId);
        if (prompt?.Destination is null)
        {
            return null;
        }

        var network = _networkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode);
        if (network is null)
        {
            return null;
        }

        var calculation = prompt.Calculate();
        var fallbackPaymentUrl = network.GenerateBIP21(prompt.Destination, calculation.Due).ToString();

        try
        {
            var storeSettings = await _storeSettingsRepository.GetAsync(invoice.StoreId).ConfigureAwait(false);
            if (storeSettings is null)
            {
                return CreatePlainBip21Response(fallbackPaymentUrl);
            }

            return await BuildPaymentUrlAsync(
                invoice.Id,
                invoice.StoreId,
                invoice.MonitoringExpiration,
                prompt.Destination,
                calculation.Due,
                storeSettings,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_logger is not null)
            {
                LogPayjoinPaymentUrlFallback(_logger, invoice.Id, ex);
            }

            return CreatePlainBip21Response(fallbackPaymentUrl);
        }
    }

    private async Task<GetBip21Response?> BuildPaymentUrlAsync(
        string invoiceId,
        string storeId,
        DateTimeOffset monitoringExpiresAt,
        string destination,
        decimal due,
        PayjoinStoreSettings storeSettings,
        CancellationToken cancellationToken)
    {
        var paymentUrl = await _payjoinUriSessionService.BuildAsync(
            PayjoinConstants.BitcoinCode,
            destination,
            due,
            storeSettings,
            storeSettings.EnabledByDefault,
            invoiceId,
            storeId,
            monitoringExpiresAt,
            cancellationToken).ConfigureAwait(false);

        return new GetBip21Response
        {
            Bip21 = paymentUrl,
            PayjoinEnabled = IsPayjoinEnabled(paymentUrl)
        };
    }

    private static GetBip21Response CreatePlainBip21Response(string fallbackPaymentUrl)
    {
        return new GetBip21Response
        {
            Bip21 = fallbackPaymentUrl,
            PayjoinEnabled = false
        };
    }

    private static bool IsPayjoinEnabled(string paymentUrl)
    {
        try
        {
            using var parsedUri = PayjoinUri.Parse(paymentUrl);
            using var _ = parsedUri.CheckPjSupported();
            return true;
        }
        catch (PjParseException)
        {
            return false;
        }
        catch (PjNotSupported)
        {
            return false;
        }
    }
}
