using System;
using BTCPayServer.Abstractions.Models;

namespace BTCPayServer.Plugins.Payjoin.Models;

public class PayjoinStoreSettingsViewModel
{
    public required string StoreId { get; set; }

    public bool EnabledByDefault { get; set; }

    public Uri? DirectoryUrl { get; set; }

    public Uri? OhttpRelayUrl { get; set; }

    public bool DemoMode { get; set; }

    public required LayoutModel LayoutModel { get; set; }
}
