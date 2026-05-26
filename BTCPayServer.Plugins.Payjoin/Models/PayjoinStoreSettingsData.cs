using System;

namespace BTCPayServer.Plugins.Payjoin.Models;

public sealed class PayjoinStoreSettingsData : PayjoinStoreSettingsInput
{
    public static PayjoinStoreSettingsData FromSettings(PayjoinStoreSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new PayjoinStoreSettingsData
        {
            PayjoinV2Enabled = settings.PayjoinV2Enabled,
            DirectoryUrl = settings.DirectoryUrl,
            OhttpRelayUrl = settings.OhttpRelayUrl,
            ColdWalletDerivationScheme = settings.ColdWalletDerivationScheme
        };
    }
}
