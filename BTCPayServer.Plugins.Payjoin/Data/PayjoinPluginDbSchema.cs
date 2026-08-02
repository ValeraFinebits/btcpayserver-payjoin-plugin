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
    internal const string AccountingBridgesTable = "AccountingBridges";
    internal const string AccountingBridgesPrimaryKey = "PK_AccountingBridges";
    internal const string ReceiverSeenInputsTable = "ReceiverSeenInputs";
    internal const string ReceiverSeenInputsPrimaryKey = "PK_ReceiverSeenInputs";

    internal const string SenderSessionsTable = "SenderSessions";
    internal const string SenderSessionsPrimaryKey = "PK_SenderSessions";
    internal const string SenderSessionEventsTable = "SenderSessionEvents";
    internal const string SenderSessionEventsPrimaryKey = "PK_SenderSessionEvents";
    internal const string SenderSessionEventsSessionForeignKey = "FK_SenderSessionEvents_SenderSessions_SenderSessionId";

    internal const string ReceiverSessionEventsInvoiceSequenceIndex = "IX_ReceiverSessionEvents_InvoiceId_Sequence";
    internal const string SenderSessionEventsSessionSequenceIndex = "IX_SenderSessionEvents_SenderSessionId_Sequence";
    internal const string SenderSessionsStoreIdIndex = "IX_SenderSessions_StoreId";
    internal const string SenderSessionsStatusCreatedAtIndex = "IX_SenderSessions_Status_CreatedAt";
    internal const string SenderSessionsOriginalTransactionIdIndex = "IX_SenderSessions_OriginalTransactionId";

    internal const string ReceiverInputReservationsOutPointIndex = "IX_ReceiverInputReservations_TransactionId_OutputIndex";
    internal const string ReceiverInputReservationsInvoiceIdIndex = "IX_ReceiverInputReservations_InvoiceId";
    internal const string ReceiverInputReservationsStoreIdIndex = "IX_ReceiverInputReservations_StoreId";
    internal const string ReceiverInputReservationsExpiresAtIndex = "IX_ReceiverInputReservations_ExpiresAt";
    internal const string AccountingBridgesInvoiceIdIndex = "IX_AccountingBridges_InvoiceId";
    internal const string AccountingBridgesStatusCreatedAtIndex = "IX_AccountingBridges_Status_CreatedAt";
    internal const string AccountingBridgesFallbackOutPointIndex = "IX_AccountingBridges_FallbackTransactionId_FallbackOutputIndex";
    internal const string AccountingBridgesExpectedFinalTransactionIdIndex = "IX_AccountingBridges_ExpectedFinalTransactionId";
    internal const string ReceiverSeenInputsOutPointIndex = "IX_ReceiverSeenInputs_TransactionId_OutputIndex";

    internal const int ReceiverAddressMaxLength = 128;
    internal const int SenderSessionIdMaxLength = 64;
    internal const int TransactionIdMaxLength = 64;
    internal const int CryptoCodeMaxLength = 16;
    internal const int PaymentMethodIdMaxLength = 64;
    internal const int BridgeFailureMessageMaxLength = 1024;
    internal const int BridgeSettlementScriptMaxLength = 512;
}
