using BTCPayServer.Abstractions.Models;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using System;

namespace BTCPayServer.Plugins.Payjoin.Models;

public class PayjoinStoreSettingsViewModel : PayjoinStoreSettingsInput
{
    public required string StoreId { get; set; }

    [BindNever]
    [ValidateNever]
    public LayoutModel LayoutModel { get; set; } = default!;

    public static PayjoinStoreSettingsViewModel FromSettings(string storeId, PayjoinStoreSettings settings, LayoutModel layoutModel)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(layoutModel);

        return new PayjoinStoreSettingsViewModel
        {
            StoreId = storeId,
            PayjoinV2Enabled = settings.PayjoinV2Enabled,
            DirectoryUrl = settings.DirectoryUrl,
            OhttpRelayUrl = settings.OhttpRelayUrl,
            ColdWalletDerivationScheme = settings.ColdWalletDerivationScheme,
            LayoutModel = layoutModel
        };
    }
}
