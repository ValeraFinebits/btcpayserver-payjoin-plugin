using System;

namespace BTCPayServer.Plugins.Payjoin.Models;

public class PayjoinStoreSettings
{
    public bool EnabledByDefault { get; set; }

    public Uri? DirectoryUrl { get; set; }

    public Uri? OhttpRelayUrl { get; set; }

    public bool DemoMode { get; set; }
}
