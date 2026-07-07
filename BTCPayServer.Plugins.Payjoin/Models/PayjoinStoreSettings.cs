using System;
using System.Collections.Generic;
using System.Linq;

namespace BTCPayServer.Plugins.Payjoin.Models;

public class PayjoinStoreSettings
{
    private static readonly Uri[] DefaultDirectoryUrlValues =
    [
        new("https://payjo.in/"),
        new("https://lets.payjo.in/")
    ];

    private static readonly Uri[] DefaultOhttpRelayUrlValues =
    [
        new("https://pj.benalleng.com"),
        new("https://pj.bobspacebkk.com"),
        new("https://payjoin.achow101.com")
    ];

    public const bool DefaultPayjoinV2Enabled = true;

    public static IReadOnlyList<Uri> DefaultDirectoryUrls { get; } = Array.AsReadOnly(DefaultDirectoryUrlValues);

    public static IReadOnlyList<Uri> DefaultOhttpRelayUrls { get; } = Array.AsReadOnly(DefaultOhttpRelayUrlValues);

    public bool PayjoinV2Enabled { get; set; } = DefaultPayjoinV2Enabled;

    public IReadOnlyList<Uri>? DirectoryUrls { get; set; } = DefaultDirectoryUrls;

    public IReadOnlyList<Uri>? OhttpRelayUrls { get; set; } = DefaultOhttpRelayUrls;

    public string? ColdWalletDerivationScheme { get; set; }

    public IReadOnlyList<Uri> GetEffectiveDirectoryUrls()
    {
        var directoryUrls = NormalizeUrls(DirectoryUrls);
        return directoryUrls.Count > 0 ? directoryUrls : [];
    }

    public IReadOnlyList<Uri> GetEffectiveOhttpRelayUrls()
    {
        var relayUrls = NormalizeUrls(OhttpRelayUrls);
        return relayUrls.Count > 0 ? relayUrls : [];
    }

    public void NormalizeUrlSettings()
    {
        DirectoryUrls = NormalizeUrls(DirectoryUrls);
        OhttpRelayUrls = NormalizeUrls(OhttpRelayUrls);
    }

    internal static IReadOnlyList<Uri> NormalizeDirectoryUrls(IEnumerable<Uri?>? directoryUrls)
    {
        return NormalizeUrls(directoryUrls);
    }

    internal static IReadOnlyList<Uri> NormalizeOhttpRelayUrls(IEnumerable<Uri?>? relayUrls)
    {
        return NormalizeUrls(relayUrls);
    }

    internal static bool IsSupportedUrl(Uri? url)
    {
        return url is { IsAbsoluteUri: true } &&
               string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<Uri> NormalizeUrls(IEnumerable<Uri?>? urls)
    {
        if (urls is null)
        {
            return [];
        }

        return urls
            .Where(IsSupportedUrl)
            .Select(static url => url!)
            .DistinctBy(static url => url.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
