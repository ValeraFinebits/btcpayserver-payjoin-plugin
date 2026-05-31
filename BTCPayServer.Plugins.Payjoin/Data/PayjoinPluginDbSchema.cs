namespace BTCPayServer.Plugins.Payjoin.Data;

internal static class PayjoinPluginDbSchema
{
    internal const string SchemaName = "BTCPayServer.Plugins.Payjoin";

    internal const string ReceiverSessionsTable = "ReceiverSessions";
    internal const string ReceiverSessionsPrimaryKey = "PK_ReceiverSessions";

    internal const string ReceiverSessionEventsTable = "ReceiverSessionEvents";
    internal const string ReceiverSessionEventsPrimaryKey = "PK_ReceiverSessionEvents";
    internal const string ReceiverSessionEventsSessionForeignKey = "FK_ReceiverSessionEvents_ReceiverSessions_InvoiceId";
    internal const string ReceiverInputReservationsTable = "ReceiverInputReservations";
    internal const string ReceiverInputReservationsPrimaryKey = "PK_ReceiverInputReservations";
    internal const string ReceiverInputReservationsSessionForeignKey = "FK_ReceiverInputReservations_ReceiverSessions_InvoiceId";

    internal const string ReceiverSessionEventsInvoiceSequenceIndex = "IX_ReceiverSessionEvents_InvoiceId_Sequence";

    internal const string ReceiverInputReservationsOutPointIndex = "IX_ReceiverInputReservations_TransactionId_OutputIndex";
    internal const string ReceiverInputReservationsInvoiceIdIndex = "IX_ReceiverInputReservations_InvoiceId";
    internal const string ReceiverInputReservationsStoreIdIndex = "IX_ReceiverInputReservations_StoreId";
    internal const string ReceiverInputReservationsExpiresAtIndex = "IX_ReceiverInputReservations_ExpiresAt";

    internal const int ReceiverAddressMaxLength = 128;
    internal const int OhttpRelayUrlMaxLength = 2048;
    internal const int TransactionIdMaxLength = 64;
}
