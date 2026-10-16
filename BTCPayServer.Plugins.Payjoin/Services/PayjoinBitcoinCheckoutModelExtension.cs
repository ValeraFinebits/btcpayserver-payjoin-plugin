using BTCPayServer.Client.Models;
using BTCPayServer.Models.InvoicingModels;
using BTCPayServer.Payments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace BTCPayServer.Plugins.Payjoin.Services;

public sealed class PayjoinBitcoinCheckoutModelExtension : IGlobalCheckoutModelExtension, IResultFilter
{
    internal const string PlainBitcoinUrlKey = "payjoinPlainBitcoinUrl";
    internal const string PlainBitcoinUrlQrKey = "payjoinPlainBitcoinUrlQR";
    internal const string PayjoinBitcoinUrlKey = "payjoinPaymentUrl";
    internal const string PayjoinBitcoinUrlQrKey = "payjoinPaymentUrlQR";
    internal const string PayjoinPaymentUrlEndpointKey = "payjoinPaymentUrlEndpoint";
    internal const string PayjoinV2EnabledKey = "payjoinV2Enabled";
    internal const string CanBootstrapKey = "payjoinCanBootstrap";

    private static readonly Action<ILogger, string, Exception?> LogCheckoutModelFailure =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1, nameof(ModifyCheckoutModel)),
            "Failed to add payjoin data to the checkout model for invoice {InvoiceId}; rendering plain BIP21.");

    private readonly Network _network;
    private readonly PayjoinSessionUriReader _payjoinSessionUriReader;
    private readonly ILogger<PayjoinBitcoinCheckoutModelExtension>? _logger;
    private CheckoutModelContext? _checkoutContext;

    public PayjoinBitcoinCheckoutModelExtension(
        BTCPayNetworkProvider networkProvider,
        PayjoinSessionUriReader payjoinSessionUriReader,
        ILogger<PayjoinBitcoinCheckoutModelExtension>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(networkProvider);
        ArgumentNullException.ThrowIfNull(payjoinSessionUriReader);

        var network = networkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode)
            ?? throw new InvalidOperationException($"Network not available for {PayjoinConstants.BitcoinCode}");
        _network = network.NBitcoinNetwork;
        _payjoinSessionUriReader = payjoinSessionUriReader;
        _logger = logger;
    }

    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _checkoutContext = context;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Runs on the checkout render path: an escaping exception would turn the payer's payment page into a 500.")]
    public void OnResultExecuting(ResultExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var model = context.Result switch
        {
            ViewResult view => view.Model as CheckoutModel,
            JsonResult json => json.Value as CheckoutModel,
            _ => null
        };
        if (_checkoutContext is null || model?.Status != "New" || !ReferenceEquals(model, _checkoutContext.Model) ||
            _checkoutContext.InvoiceEntity.GetInvoiceState().Status != InvoiceStatus.New)
        {
            return;
        }

        try
        {
            context.HttpContext.RequestAborted.ThrowIfCancellationRequested();
            ApplyPayjoinCheckoutModel(_checkoutContext);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            if (_logger is not null)
            {
                LogCheckoutModelFailure(_logger, _checkoutContext.InvoiceEntity.Id, e);
            }
        }
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
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
        var bitcoinId = PaymentTypes.CHAIN.GetPaymentMethodId(PayjoinConstants.BitcoinCode);
        var prompt = context.InvoiceEntity.GetPaymentPrompt(bitcoinId);
        if (prompt?.Activated != true)
            prompt = null;

        ApplyPayjoinCheckoutModel(
            context.Model,
            payjoinV2Enabled,
            paymentUrlEndpoint,
            context.InvoiceEntity.Id,
            prompt?.Destination,
            prompt?.Calculate().Due,
            _network,
            _payjoinSessionUriReader);
    }

    internal static void ApplyPayjoinCheckoutModel(
        CheckoutModel model,
        bool payjoinV2Enabled,
        string? paymentUrlEndpoint,
        string invoiceId,
        string? destination,
        decimal? due,
        Network network,
        PayjoinSessionUriReader payjoinSessionUriReader)
    {
        ArgumentNullException.ThrowIfNull(payjoinSessionUriReader);
        model.AdditionalData ??= [];
        model.AdditionalData.Remove(PayjoinBitcoinUrlKey);
        model.AdditionalData.Remove(PayjoinBitcoinUrlQrKey);

        if (!ApplyPayjoinCheckoutMetadata(model, paymentUrlEndpoint, payjoinV2Enabled))
        {
            return;
        }

        if (!payjoinV2Enabled)
        {
            return;
        }

        var plainUrl = model.InvoiceBitcoinUrl ?? string.Empty;
        if (due is not { } invoiceDue || invoiceDue <= 0m || destination is null ||
            plainUrl.Contains('#', StringComparison.Ordinal) ||
            !MatchesBitcoinPayment(PayjoinBip21.ReplacePayjoinQueryParameters(plainUrl, []), destination, invoiceDue, network))
        {
            return;
        }

        var payjoinUri = payjoinSessionUriReader.TryGetExistingPayjoinUri(invoiceId, destination);
        model.AdditionalData[CanBootstrapKey] = payjoinUri is null;
        if (payjoinUri is not null && MatchesBitcoinPayment(payjoinUri, destination, invoiceDue, network))
        {
            ApplyPayjoinPaymentUrl(model, payjoinUri);
        }
    }

    private static bool MatchesBitcoinPayment(string uri, string destination, decimal due, Network network)
    {
        var queryIndex = uri.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex < 0 || uri.Contains('#', StringComparison.Ordinal) ||
            uri[(queryIndex + 1)..].Split('&').Count(segment =>
                string.Equals(System.Uri.UnescapeDataString(segment.Split('=')[0]), "amount", StringComparison.OrdinalIgnoreCase)) != 1)
            return false;

        try
        {
            using var parsed = global::Payjoin.Uri.Parse(uri);
            return PayjoinUriSessionService.HasExpectedAmount(parsed.AmountSats(), due) &&
                BitcoinAddress.Create(parsed.Address(), network).ScriptPubKey == BitcoinAddress.Create(destination, network).ScriptPubKey;
        }
        catch (global::Payjoin.UniffiException e)
        {
            if (e is IDisposable disposable)
                disposable.Dispose();
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal static void ApplyPayjoinPaymentUrl(CheckoutModel model, string payjoinUri)
    {
        ArgumentNullException.ThrowIfNull(model);

        var plainUrl = model.InvoiceBitcoinUrl ?? string.Empty;
        if (string.IsNullOrWhiteSpace(plainUrl))
        {
            return;
        }

        if (!PayjoinBip21.IsPublishableMergedPaymentUrl(payjoinUri))
        {
            return;
        }

        model.AdditionalData ??= [];
        model.AdditionalData[PayjoinBitcoinUrlKey] = JToken.FromObject(payjoinUri);
        model.AdditionalData[PayjoinBitcoinUrlQrKey] = JToken.FromObject(payjoinUri);
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
        model.AdditionalData[PlainBitcoinUrlQrKey] = JToken.FromObject(plainUrl);
        model.AdditionalData[PayjoinPaymentUrlEndpointKey] = JToken.FromObject(paymentUrlEndpoint);
        model.AdditionalData[PayjoinV2EnabledKey] = JToken.FromObject(payjoinV2Enabled);
        model.AdditionalData[CanBootstrapKey] = false;
        return true;
    }
}
