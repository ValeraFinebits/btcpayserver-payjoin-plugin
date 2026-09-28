using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.Payjoin.Services;

namespace BTCPayServer.Plugins.Payjoin.Tests;

internal static class TestSessionStates
{
    internal const string DefaultInvoiceId = "invoice-1";
    internal const string DefaultStoreId = "store-1";
    internal const string DefaultReceiverAddress = "bcrt1qexampleaddress0000000000000000000000000";

    private static readonly string[] DefaultEvents = ["{\"event\":\"created\"}"];

    internal static PayjoinReceiverSessionState Create(
        string invoiceId = DefaultInvoiceId,
        string storeId = DefaultStoreId,
        string receiverAddress = DefaultReceiverAddress,
        TimeSpan? monitoringRemaining = null,
        DateTimeOffset? updatedAt = null,
        bool isCloseRequested = false,
        InvoiceStatus? closeInvoiceStatus = null,
        DateTimeOffset? closeRequestedAt = null,
        bool initializedPollAfterCloseRequestConsumed = false,
        string? contributedInputTransactionId = null,
        long? contributedInputOutputIndex = null,
        string[]? events = null,
        string? payjoinUri = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new PayjoinReceiverSessionState(
            invoiceId,
            storeId,
            receiverAddress,
            now + (monitoringRemaining ?? TimeSpan.FromHours(1)),
            now,
            updatedAt ?? now,
            isCloseRequested,
            closeInvoiceStatus,
            closeRequestedAt,
            initializedPollAfterCloseRequestConsumed,
            contributedInputTransactionId,
            contributedInputOutputIndex,
            events ?? DefaultEvents,
            payjoinUri);
    }
}
