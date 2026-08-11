using BTCPayServer.Client.Models;
using BTCPayServer.Models.InvoicingModels;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Services;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace BTCPayServer.Plugins.Payjoin.Services;

public sealed class PayjoinBitcoinCheckoutModelExtension : ICheckoutModelExtension
{
    internal const string PlainBitcoinUrlKey = "payjoinPlainBitcoinUrl";
    internal const string PlainBitcoinUrlQrKey = "payjoinPlainBitcoinUrlQR";
    internal const string PayjoinBitcoinUrlKey = "payjoinPaymentUrl";
    internal const string PayjoinBitcoinUrlQrKey = "payjoinPaymentUrlQR";
    internal const string PayjoinPaymentUrlEndpointKey = "payjoinPaymentUrlEndpoint";
    internal const string PayjoinV2EnabledKey = "payjoinV2Enabled";

    private static readonly Action<ILogger, string, Exception?> LogCheckoutModelFailure =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1, nameof(ModifyCheckoutModel)),
            "Failed to add payjoin data to the checkout model for invoice {InvoiceId}; rendering plain BIP21.");

    private readonly BitcoinCheckoutModelExtension _innerExtension;
    private readonly PayjoinSessionUriReader _payjoinSessionUriReader;
    private readonly ILogger<PayjoinBitcoinCheckoutModelExtension>? _logger;

    public PayjoinBitcoinCheckoutModelExtension(
        BTCPayNetworkProvider networkProvider,
        IEnumerable<IPaymentLinkExtension> paymentLinkExtensions,
        DisplayFormatter displayFormatter,
        PayjoinSessionUriReader payjoinSessionUriReader,
        ILogger<PayjoinBitcoinCheckoutModelExtension>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(networkProvider);
        ArgumentNullException.ThrowIfNull(paymentLinkExtensions);
        ArgumentNullException.ThrowIfNull(displayFormatter);
        ArgumentNullException.ThrowIfNull(payjoinSessionUriReader);

        PaymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(PayjoinConstants.BitcoinCode);
        var network = networkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode)
            ?? throw new InvalidOperationException($"Network not available for {PayjoinConstants.BitcoinCode}");
        _innerExtension = new BitcoinCheckoutModelExtension(PaymentMethodId, network, paymentLinkExtensions, displayFormatter);
        _payjoinSessionUriReader = payjoinSessionUriReader;
        _logger = logger;
    }

    public PaymentMethodId PaymentMethodId { get; }
    public string Image => _innerExtension.Image;
    public string Badge => _innerExtension.Badge;

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Runs on the checkout render path: an escaping exception would turn the payer's payment page into a 500.")]
    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _innerExtension.ModifyCheckoutModel(context);

        if (context.InvoiceEntity.GetInvoiceState().Status != InvoiceStatus.New)
        {
            return;
        }

        try
        {
            ApplyPayjoinCheckoutModel(context);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            if (_logger is not null)
            {
                LogCheckoutModelFailure(_logger, context.InvoiceEntity.Id, e);
            }
        }
    }

    private void ApplyPayjoinCheckoutModel(CheckoutModelContext context)
    {
        var storeSettings = PayjoinStoreSettingsRepository.ReadSettings(context.StoreBlob);
        var paymentUrlEndpoint = context.UrlHelper.Action(new UrlActionContext
        {
            Action = "GetInvoicePaymentUrl",
            Controller = "UIPayJoin",
            Values = new { invoiceId = context.InvoiceEntity.Id }
        });

        var payjoinV2Enabled = storeSettings?.PayjoinV2Enabled == true;
        var prompt = payjoinV2Enabled ? context.Prompt : null;

        ApplyPayjoinCheckoutModel(
            context.Model,
            payjoinV2Enabled,
            paymentUrlEndpoint,
            context.InvoiceEntity.Id,
            prompt?.Destination,
            prompt?.Calculate().Due,
            _payjoinSessionUriReader);
    }

    internal static void ApplyPayjoinCheckoutModel(
        CheckoutModel model,
        bool payjoinV2Enabled,
        string? paymentUrlEndpoint,
        string invoiceId,
        string? destination,
        decimal? due,
        PayjoinSessionUriReader payjoinSessionUriReader)
    {
        ArgumentNullException.ThrowIfNull(payjoinSessionUriReader);

        if (!ApplyPayjoinCheckoutMetadata(model, paymentUrlEndpoint, payjoinV2Enabled))
        {
            return;
        }

        if (!payjoinV2Enabled)
        {
            return;
        }

        if (due is not { } invoiceDue || invoiceDue <= 0m || destination is null)
        {
            return;
        }

        var payjoinUri = payjoinSessionUriReader.TryGetExistingPayjoinUri(invoiceId, destination);
        if (payjoinUri is not null)
        {
            ApplyPayjoinPaymentUrl(model, payjoinUri);
        }
    }

    internal static void ApplyPayjoinPaymentUrl(CheckoutModel model, string payjoinUri)
    {
        ArgumentNullException.ThrowIfNull(model);

        var plainUrl = model.InvoiceBitcoinUrl ?? string.Empty;
        var plainUrlQr = model.InvoiceBitcoinUrlQR ?? string.Empty;

        if (string.IsNullOrWhiteSpace(plainUrl) || string.IsNullOrWhiteSpace(plainUrlQr))
        {
            return;
        }

        var payjoinUrl = PayjoinBip21.MergePayjoinIntoPaymentUrl(plainUrl, payjoinUri);
        var payjoinUrlQr = PayjoinBip21.MergePayjoinIntoPaymentUrl(plainUrlQr, payjoinUri);

        if (!PayjoinBip21.IsPublishableMergedPaymentUrl(payjoinUrl) ||
            !PayjoinBip21.IsPublishableMergedPaymentUrl(payjoinUrlQr))
        {
            return;
        }

        model.AdditionalData ??= [];
        model.AdditionalData[PayjoinBitcoinUrlKey] = JToken.FromObject(payjoinUrl);
        model.AdditionalData[PayjoinBitcoinUrlQrKey] = JToken.FromObject(payjoinUrlQr);
    }

    internal static bool ApplyPayjoinCheckoutMetadata(CheckoutModel model, string? paymentUrlEndpoint, bool payjoinV2Enabled)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (string.IsNullOrWhiteSpace(paymentUrlEndpoint))
        {
            return false;
        }

        var plainUrl = model.InvoiceBitcoinUrl ?? string.Empty;

        model.AdditionalData ??= [];
        model.AdditionalData[PlainBitcoinUrlKey] = JToken.FromObject(plainUrl);
        model.AdditionalData[PlainBitcoinUrlQrKey] = JToken.FromObject(model.InvoiceBitcoinUrlQR ?? string.Empty);
        model.AdditionalData[PayjoinPaymentUrlEndpointKey] = JToken.FromObject(paymentUrlEndpoint);
        model.AdditionalData[PayjoinV2EnabledKey] = JToken.FromObject(payjoinV2Enabled);
        return true;
    }
}
