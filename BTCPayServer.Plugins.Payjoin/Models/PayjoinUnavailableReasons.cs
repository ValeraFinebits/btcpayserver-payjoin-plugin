namespace BTCPayServer.Plugins.Payjoin.Models;

public static class PayjoinUnavailableReasons
{
    public const string DisabledByStoreSettings = "payjoin is disabled by store settings";
    public const string StoreSettingsUnavailable = "store settings are unavailable";
    public const string DirectoryUrlsMissing = "directory URLs are missing";
    public const string OhttpRelayUrlsMissing = "OHTTP relay URLs are missing";
    public const string InvoiceAmountNotPositive = "invoice amount is not positive";
    public const string NoConfirmedReceiverInputs = "no confirmed receiver inputs are available";
    public const string OhttpKeysUnavailable = "OHTTP keys are unavailable from all configured relays";
    public const string EmptyPayjoinUri = "payjoin URI generation returned an empty value";
    public const string PayjoinUriWithoutEndpoint = "payjoin URI does not advertise payjoin support";
    public const string ReceiverSessionBuildFailed = "receiver session build failed";
    public const string PaymentUrlGenerationFailed = "payjoin payment URL generation failed";
    public const string PayjoinUriMergeLostEndpoint = "merging the payjoin endpoint into the invoice BIP21 produced a plain URL";
    public const string PayjoinUriMergeCheckFaulted = "checking the merged payjoin URL failed unexpectedly";
    public const string SessionAddressOutdated = "the receiver session for this invoice was built for a different address";
    public const string SessionMidNegotiation = "the receiver session is completing a payjoin negotiation";
    public const string SessionNoLongerServable = "the receiver session is closed or past its monitoring window";
}
