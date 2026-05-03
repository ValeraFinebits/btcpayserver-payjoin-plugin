namespace BTCPayServer.Plugins.Payjoin.Data;

internal static class PayjoinPluginDbSchema
{
    internal const string SchemaName = "BTCPayServer.Plugins.Payjoin";
    internal const string PluginRecordsTable = "PluginRecords";
    internal const string ReceiverSessionsTable = "ReceiverSessions";
    internal const string ReceiverSessionEventsTable = "ReceiverSessionEvents";
    internal const string ReceiverSessionEventsInvoiceSequenceIndex = "IX_ReceiverSessionEvents_InvoiceId_Sequence";

    internal const int ReceiverAddressMaxLength = 128;
    internal const int OhttpRelayUrlMaxLength = 2048;
    internal const int TransactionIdMaxLength = 64;
}
