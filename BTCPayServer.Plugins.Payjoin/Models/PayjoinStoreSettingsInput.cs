using System;
using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.Payjoin.Models;

public abstract class PayjoinStoreSettingsInput
{
    public bool PayjoinV2Enabled { get; set; } = PayjoinStoreSettings.DefaultPayjoinV2Enabled;

    [Required]
    public Uri? DirectoryUrl { get; set; } = PayjoinStoreSettings.DefaultDirectoryUrl;

    [Required]
    public Uri? OhttpRelayUrl { get; set; } = PayjoinStoreSettings.DefaultOhttpRelayUrl;

    public string? ColdWalletDerivationScheme { get; set; }

    public PayjoinStoreSettings ToSettings(string? coldWalletDerivationScheme = null)
    {
        return new PayjoinStoreSettings
        {
            PayjoinV2Enabled = PayjoinV2Enabled,
            DirectoryUrl = DirectoryUrl,
            OhttpRelayUrl = OhttpRelayUrl,
            ColdWalletDerivationScheme = coldWalletDerivationScheme ?? ColdWalletDerivationScheme
        };
    }
}
