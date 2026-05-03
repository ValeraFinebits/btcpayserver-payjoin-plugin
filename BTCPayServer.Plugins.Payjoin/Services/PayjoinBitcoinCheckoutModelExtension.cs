using BTCPayServer.BIP78.Sender;
using BTCPayServer.Client.Models;
using BTCPayServer.Models.InvoicingModels;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BTCPayServer.Plugins.Payjoin.Services;

public sealed class PayjoinBitcoinCheckoutModelExtension : ICheckoutModelExtension
{
    private const string CryptoCode = "BTC";
    internal const string OutputSubstitutionParameterKey = "pjos";
    internal const string PlainBitcoinUrlKey = "payjoinPlainBitcoinUrl";
    internal const string PlainBitcoinUrlQrKey = "payjoinPlainBitcoinUrlQR";
    internal const string PayjoinBitcoinUrlKey = "payjoinPaymentUrl";
    internal const string PayjoinBitcoinUrlQrKey = "payjoinPaymentUrlQR";
    internal const string PayjoinPaymentUrlEndpointKey = "payjoinPaymentUrlEndpoint";
    internal const string PayjoinDefaultEnabledKey = "payjoinEnabledByDefault";
    private static readonly string[] PayjoinParameterKeys = [OutputSubstitutionParameterKey, PayjoinClient.BIP21EndpointKey];
    private readonly BitcoinCheckoutModelExtension _innerExtension;

    public PayjoinBitcoinCheckoutModelExtension(
        BTCPayNetworkProvider networkProvider,
        IEnumerable<IPaymentLinkExtension> paymentLinkExtensions,
        DisplayFormatter displayFormatter)
    {
        ArgumentNullException.ThrowIfNull(networkProvider);
        ArgumentNullException.ThrowIfNull(paymentLinkExtensions);
        ArgumentNullException.ThrowIfNull(displayFormatter);

        PaymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(CryptoCode);
        var network = networkProvider.GetNetwork<BTCPayNetwork>(CryptoCode)
            ?? throw new InvalidOperationException($"Network not available for {CryptoCode}");
        _innerExtension = new BitcoinCheckoutModelExtension(PaymentMethodId, network, paymentLinkExtensions, displayFormatter);
    }

    public PaymentMethodId PaymentMethodId { get; }
    public string Image => _innerExtension.Image;
    public string Badge => _innerExtension.Badge;

    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _innerExtension.ModifyCheckoutModel(context);

        if (context.InvoiceEntity.GetInvoiceState().Status != InvoiceStatus.New)
        {
            return;
        }

        var storeSettings = PayjoinStoreSettingsRepository.ReadSettings(context.StoreBlob);
        var paymentUrlEndpoint = context.UrlHelper.Action(new UrlActionContext
        {
            Action = "GetInvoicePaymentUrl",
            Controller = "UIPayJoin",
            Values = new { invoiceId = context.InvoiceEntity.Id }
        });
        ApplyPayjoinCheckoutMetadata(context.Model, paymentUrlEndpoint, storeSettings.EnabledByDefault);
    }

    internal static void ApplyPayjoinCheckoutMetadata(CheckoutModel model, string? paymentUrlEndpoint, bool payjoinEnabledByDefault)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (string.IsNullOrWhiteSpace(paymentUrlEndpoint))
        {
            return;
        }

        model.AdditionalData ??= new Dictionary<string, JToken>();
        model.AdditionalData[PlainBitcoinUrlKey] = JToken.FromObject(model.InvoiceBitcoinUrl ?? string.Empty);
        model.AdditionalData[PlainBitcoinUrlQrKey] = JToken.FromObject(model.InvoiceBitcoinUrlQR ?? string.Empty);
        model.AdditionalData[PayjoinPaymentUrlEndpointKey] = JToken.FromObject(paymentUrlEndpoint);
        model.AdditionalData[PayjoinDefaultEnabledKey] = JToken.FromObject(payjoinEnabledByDefault);
    }

    internal static void ApplyPayjoinPaymentUrl(CheckoutModel model, GetBip21Response paymentUrl, bool payjoinEnabledByDefault)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(paymentUrl);

        if (!paymentUrl.PayjoinEnabled)
        {
            return;
        }

        model.AdditionalData ??= new Dictionary<string, JToken>();
        var plainUrl = model.InvoiceBitcoinUrl ?? string.Empty;
        var plainUrlQr = model.InvoiceBitcoinUrlQR ?? string.Empty;
        var payjoinUrl = MergePayjoinIntoPaymentUrl(plainUrl, paymentUrl.Bip21);
        var payjoinUrlQr = MergePayjoinIntoPaymentUrl(plainUrlQr, paymentUrl.Bip21);

        model.AdditionalData[PlainBitcoinUrlKey] = JToken.FromObject(plainUrl);
        model.AdditionalData[PlainBitcoinUrlQrKey] = JToken.FromObject(plainUrlQr);
        model.AdditionalData[PayjoinBitcoinUrlKey] = JToken.FromObject(payjoinUrl);
        model.AdditionalData[PayjoinBitcoinUrlQrKey] = JToken.FromObject(payjoinUrlQr);
        model.AdditionalData[PayjoinDefaultEnabledKey] = JToken.FromObject(payjoinEnabledByDefault);
        model.InvoiceBitcoinUrl = payjoinUrl;
        model.InvoiceBitcoinUrlQR = payjoinUrlQr;
    }

    internal static string MergePayjoinIntoPaymentUrl(string baseUrl, string payjoinUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return payjoinUrl;
        }

        if (string.IsNullOrWhiteSpace(payjoinUrl))
        {
            return baseUrl;
        }

        var endpointParameter = ExtractQueryParameter(payjoinUrl, PayjoinClient.BIP21EndpointKey);
        if (endpointParameter is null)
        {
            return ReplacePayjoinQueryParameters(baseUrl, []);
        }

        var payjoinParameters = new List<string>();
        var outputSubstitutionParameter = ExtractQueryParameter(payjoinUrl, OutputSubstitutionParameterKey);
        if (outputSubstitutionParameter is not null)
        {
            payjoinParameters.Add(outputSubstitutionParameter);
        }

        payjoinParameters.Add(endpointParameter);
        return ReplacePayjoinQueryParameters(baseUrl, payjoinParameters);
    }

    internal static string? ExtractQueryParameter(string url, string key)
    {
        var query = GetQuery(url);
        if (string.IsNullOrEmpty(query))
        {
            return null;
        }

        foreach (var segment in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (HasQueryKey(segment, key))
            {
                return segment;
            }
        }

        return null;
    }

    internal static string ReplacePayjoinQueryParameters(string url, IReadOnlyList<string> rawSegments)
    {
        var querySeparatorIndex = url.IndexOf('?', StringComparison.Ordinal);
        var prefix = querySeparatorIndex >= 0 ? url[..querySeparatorIndex] : url;
        var query = querySeparatorIndex >= 0 ? url[(querySeparatorIndex + 1)..] : string.Empty;

        var segments = new List<string>();
        if (!string.IsNullOrEmpty(query))
        {
            foreach (var segment in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!HasAnyQueryKey(segment, PayjoinParameterKeys))
                {
                    segments.Add(segment);
                }
            }
        }

        if (rawSegments.Count > 0)
        {
            var lightningIndex = segments.FindIndex(segment => HasQueryKey(segment, "lightning"));
            var insertIndex = lightningIndex >= 0 ? lightningIndex : segments.Count;
            segments.InsertRange(insertIndex, rawSegments.Where(segment => !string.IsNullOrWhiteSpace(segment)));
        }

        return segments.Count == 0
            ? prefix
            : $"{prefix}?{string.Join("&", segments)}";
    }

    private static string? GetQuery(string url)
    {
        var querySeparatorIndex = url.IndexOf('?', StringComparison.Ordinal);
        return querySeparatorIndex >= 0 && querySeparatorIndex < url.Length - 1
            ? url[(querySeparatorIndex + 1)..]
            : null;
    }

    private static bool HasQueryKey(string segment, string key)
    {
        var keyValueSeparatorIndex = segment.IndexOf('=', StringComparison.Ordinal);
        var segmentKey = keyValueSeparatorIndex >= 0 ? segment[..keyValueSeparatorIndex] : segment;
        return string.Equals(segmentKey, key, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasAnyQueryKey(string segment, IEnumerable<string> keys)
    {
        return keys.Any(key => HasQueryKey(segment, key));
    }
}
