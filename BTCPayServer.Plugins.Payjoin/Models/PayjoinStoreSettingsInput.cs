using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;

namespace BTCPayServer.Plugins.Payjoin.Models;

public abstract class PayjoinStoreSettingsInput
{
    internal readonly record struct UrlListParseError(int LineNumber, string Value, string Message);

    internal readonly record struct UrlListParseResult(IReadOnlyList<Uri> Urls, IReadOnlyList<UrlListParseError> Errors);

    public bool PayjoinV2Enabled { get; set; } = PayjoinStoreSettings.DefaultPayjoinV2Enabled;

    public IReadOnlyList<Uri>? DirectoryUrls { get; set; } = PayjoinStoreSettings.DefaultDirectoryUrls;

    public string? DirectoryUrlsText { get; set; }

    public IReadOnlyList<Uri>? OhttpRelayUrls { get; set; } = PayjoinStoreSettings.DefaultOhttpRelayUrls;

    public string? OhttpRelayUrlsText { get; set; }

    public string? ColdWalletDerivationScheme { get; set; }

    [Range(PayjoinStoreSettings.MinFeeRateSatPerVb, PayjoinStoreSettings.MaxFeeRateSatPerVbLimit,
        ErrorMessage = "The maximum fee rate must be between 1 and 100000 sat/vB.")]
    public long? MaxFeeRateSatPerVb { get; set; }

    internal IReadOnlyList<Uri> GetEffectiveDirectoryUrls()
    {
        return ParseDirectoryUrlsText(DirectoryUrlsText, DirectoryUrls);
    }

    internal IReadOnlyList<Uri> GetEffectiveOhttpRelayUrls()
    {
        return ParseOhttpRelayUrlsText(OhttpRelayUrlsText, OhttpRelayUrls);
    }

    public PayjoinStoreSettings ToSettings(string? coldWalletDerivationScheme = null)
    {
        var directoryUrls = GetEffectiveDirectoryUrls();
        var ohttpRelayUrls = GetEffectiveOhttpRelayUrls();

        var settings = new PayjoinStoreSettings
        {
            PayjoinV2Enabled = PayjoinV2Enabled,
            DirectoryUrls = directoryUrls,
            OhttpRelayUrls = ohttpRelayUrls,
            ColdWalletDerivationScheme = coldWalletDerivationScheme ?? ColdWalletDerivationScheme,
            MaxFeeRateSatPerVb = MaxFeeRateSatPerVb
        };
        settings.NormalizeUrlSettings();
        return settings;
    }

    internal static string FormatDirectoryUrlsText(IEnumerable<Uri>? directoryUrls)
    {
        return string.Join(Environment.NewLine, PayjoinStoreSettings.NormalizeDirectoryUrls(directoryUrls).Select(static directoryUrl => directoryUrl.AbsoluteUri));
    }

    internal static IReadOnlyList<Uri> ParseDirectoryUrlsText(string? directoryUrlsText, IEnumerable<Uri?>? fallbackDirectoryUrls = null)
    {
        return ParseDirectoryUrlsTextWithErrors(directoryUrlsText, fallbackDirectoryUrls).Urls;
    }

    internal static UrlListParseResult ParseDirectoryUrlsTextWithErrors(string? directoryUrlsText, IEnumerable<Uri?>? fallbackDirectoryUrls = null)
    {
        if (directoryUrlsText is null)
        {
            return new UrlListParseResult(PayjoinStoreSettings.NormalizeDirectoryUrls(fallbackDirectoryUrls), []);
        }

        return ParseUrlListTextWithErrors(directoryUrlsText, PayjoinStoreSettings.NormalizeDirectoryUrls);
    }

    internal static string FormatOhttpRelayUrlsText(IEnumerable<Uri>? relayUrls)
    {
        return string.Join(Environment.NewLine, PayjoinStoreSettings.NormalizeOhttpRelayUrls(relayUrls).Select(static relayUrl => relayUrl.AbsoluteUri));
    }

    internal static IReadOnlyList<Uri> ParseOhttpRelayUrlsText(string? relayUrlsText, IEnumerable<Uri?>? fallbackRelayUrls = null)
    {
        return ParseOhttpRelayUrlsTextWithErrors(relayUrlsText, fallbackRelayUrls).Urls;
    }

    internal static UrlListParseResult ParseOhttpRelayUrlsTextWithErrors(string? relayUrlsText, IEnumerable<Uri?>? fallbackRelayUrls = null)
    {
        if (relayUrlsText is null)
        {
            return new UrlListParseResult(PayjoinStoreSettings.NormalizeOhttpRelayUrls(fallbackRelayUrls), []);
        }

        return ParseUrlListTextWithErrors(relayUrlsText, PayjoinStoreSettings.NormalizeOhttpRelayUrls);
    }

    private static UrlListParseResult ParseUrlListTextWithErrors(
        string urlsText,
        Func<IEnumerable<Uri?>?, IReadOnlyList<Uri>> normalizeUrls)
    {
        var urls = new List<Uri?>();
        var errors = new List<UrlListParseError>();
        var normalizedText = urlsText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalizedText.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (Uri.TryCreate(line, UriKind.Absolute, out var url) && PayjoinStoreSettings.IsSupportedUrl(url))
            {
                urls.Add(url);
                continue;
            }

            errors.Add(new UrlListParseError(i + 1, line, "Only absolute HTTPS URLs are allowed."));
        }

        return new UrlListParseResult(normalizeUrls(urls), errors);
    }
}
