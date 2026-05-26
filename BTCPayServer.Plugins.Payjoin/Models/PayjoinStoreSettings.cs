using System;

namespace BTCPayServer.Plugins.Payjoin.Models;

public class PayjoinStoreSettings
{
    public const bool DefaultPayjoinV2Enabled = true;

    public static Uri DefaultDirectoryUrl { get; } = new("https://payjo.in/");

    public static Uri DefaultOhttpRelayUrl { get; } = new("https://pj.bobspacebkk.com");

    public bool PayjoinV2Enabled { get; set; } = DefaultPayjoinV2Enabled;

    public Uri? DirectoryUrl { get; set; } = DefaultDirectoryUrl;

    public Uri? OhttpRelayUrl { get; set; } = DefaultOhttpRelayUrl;

    public string? ColdWalletDerivationScheme { get; set; }
}
