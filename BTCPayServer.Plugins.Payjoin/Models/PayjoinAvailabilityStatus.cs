namespace BTCPayServer.Plugins.Payjoin.Models;

public enum PayjoinAvailabilityStatus
{
    TemporarilyUnavailable,
    Active,
    DisabledByStore,
    MerchantRequirementsUnmet,
    InvoiceNotPayable
}
