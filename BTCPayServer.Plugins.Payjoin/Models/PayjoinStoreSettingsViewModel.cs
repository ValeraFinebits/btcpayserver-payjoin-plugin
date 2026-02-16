using BTCPayServer.Abstractions.Models;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using System;

namespace BTCPayServer.Plugins.Payjoin.Models;

public class PayjoinStoreSettingsViewModel
{
    public required string StoreId { get; set; }

    public bool EnabledByDefault { get; set; }

    public Uri? DirectoryUrl { get; set; }

    public Uri? OhttpRelayUrl { get; set; }

    public bool DemoMode { get; set; }

    [BindNever]
    [ValidateNever]
    public LayoutModel LayoutModel { get; set; } = default!;
}
