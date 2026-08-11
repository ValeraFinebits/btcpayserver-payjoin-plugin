using BTCPayServer.BIP78.Sender;
using Payjoin;
using System;
using System.Collections.Generic;
using System.Linq;
using PayjoinUri = Payjoin.Uri;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal enum PayjoinReplayedUriVerdict
{
    Empty,
    NoPayjoinEndpoint,
    MergeLostEndpoint,
    Servable
}

internal static class PayjoinBip21
{
    internal static PayjoinReplayedUriVerdict JudgeReplayedUri(
        string? payjoinUri,
        string invoiceBip21,
        out string mergedPaymentUrl,
        out UniffiException? mergeFault)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invoiceBip21);

        mergedPaymentUrl = invoiceBip21;
        mergeFault = null;

        if (string.IsNullOrWhiteSpace(payjoinUri))
        {
            return PayjoinReplayedUriVerdict.Empty;
        }

        var merged = MergePayjoinIntoPaymentUrl(invoiceBip21, payjoinUri);
        bool mergedIsServable;
        try
        {
            mergedIsServable = HasSupportedPayjoinEndpoint(merged);
        }
        catch (UniffiException e)
        {
            mergeFault = e;
            return PayjoinReplayedUriVerdict.MergeLostEndpoint;
        }

        if (mergedIsServable)
        {
            mergedPaymentUrl = merged;
            return PayjoinReplayedUriVerdict.Servable;
        }

        return HasSupportedPayjoinEndpoint(payjoinUri)
            ? PayjoinReplayedUriVerdict.MergeLostEndpoint
            : PayjoinReplayedUriVerdict.NoPayjoinEndpoint;
    }

    internal const string OutputSubstitutionParameterKey = "pjos";
    private static readonly string[] PayjoinParameterKeys = [OutputSubstitutionParameterKey, PayjoinClient.BIP21EndpointKey];

    internal static bool HasSupportedPayjoinEndpoint(string paymentUrl)
    {
        try
        {
            using var parsedUri = PayjoinUri.Parse(paymentUrl);
            using var _ = parsedUri.CheckPjSupported();
            return true;
        }
        catch (UriParseException e)
        {
            e.Dispose();
            return false;
        }
        catch (PjNotSupported e)
        {
            e.Dispose();
            return false;
        }
    }

    internal static bool IsPublishableMergedPaymentUrl(string paymentUrl)
    {
        try
        {
            return HasSupportedPayjoinEndpoint(paymentUrl);
        }
        catch (UniffiException e)
        {
            if (e is IDisposable disposable)
            {
                disposable.Dispose();
            }

            return false;
        }
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
