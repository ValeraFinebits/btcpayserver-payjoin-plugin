using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;

namespace BTCPayServer.Plugins.Payjoin.Models;

public sealed class PayjoinStoreSettingsData
{
    public bool PayjoinV2Enabled { get; set; } = PayjoinStoreSettings.DefaultPayjoinV2Enabled;

    public IReadOnlyList<Uri>? DirectoryUrls { get; set; }

    public IReadOnlyList<Uri>? OhttpRelayUrls { get; set; }

    public string? ColdWalletDerivationScheme { get; set; }

    // The same bound the settings form applies. Without it an API client could store a cap the
    // UI would reject, and the two surfaces would disagree about a valid fee rate.
    [Range(PayjoinStoreSettings.MinFeeRateSatPerVb, PayjoinStoreSettings.MaxFeeRateSatPerVbLimit,
        ErrorMessage = "The maximum fee rate must be between 1 and 100000 sat/vB.")]
    public long? MaxFeeRateSatPerVb { get; set; }

    internal IReadOnlyList<Uri?> GetInvalidDirectoryUrls()
    {
        return GetInvalidUrls(DirectoryUrls);
    }

    internal IReadOnlyList<Uri?> GetInvalidOhttpRelayUrls()
    {
        return GetInvalidUrls(OhttpRelayUrls);
    }

    public PayjoinStoreSettings ToSettings(string? coldWalletDerivationScheme = null)
    {
        var settings = new PayjoinStoreSettings
        {
            PayjoinV2Enabled = PayjoinV2Enabled,
            DirectoryUrls = PayjoinStoreSettings.NormalizeDirectoryUrls(DirectoryUrls),
            OhttpRelayUrls = PayjoinStoreSettings.NormalizeOhttpRelayUrls(OhttpRelayUrls),
            ColdWalletDerivationScheme = coldWalletDerivationScheme ?? ColdWalletDerivationScheme,
            MaxFeeRateSatPerVb = MaxFeeRateSatPerVb
        };
        settings.NormalizeUrlSettings();
        return settings;
    }

    public static PayjoinStoreSettingsData FromSettings(PayjoinStoreSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new PayjoinStoreSettingsData
        {
            PayjoinV2Enabled = settings.PayjoinV2Enabled,
            DirectoryUrls = settings.GetEffectiveDirectoryUrls(),
            OhttpRelayUrls = settings.GetEffectiveOhttpRelayUrls(),
            ColdWalletDerivationScheme = settings.ColdWalletDerivationScheme,
            MaxFeeRateSatPerVb = settings.MaxFeeRateSatPerVb
        };
    }

    private static IReadOnlyList<Uri?> GetInvalidUrls(IEnumerable<Uri?>? urls)
    {
        if (urls is null)
        {
            return [];
        }

        return urls.Where(static url => !PayjoinStoreSettings.IsSupportedUrl(url)).ToArray();
    }
}
