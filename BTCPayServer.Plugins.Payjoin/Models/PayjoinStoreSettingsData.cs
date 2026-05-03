using System;
using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.Payjoin.Models;

public sealed class PayjoinStoreSettingsData
{
    public bool EnabledByDefault { get; set; }

    [Required]
    public Uri? DirectoryUrl { get; set; } = PayjoinStoreSettings.DefaultDirectoryUrl;

    [Required]
    public Uri? OhttpRelayUrl { get; set; } = PayjoinStoreSettings.DefaultOhttpRelayUrl;

    public string? ColdWalletDerivationScheme { get; set; }
}
