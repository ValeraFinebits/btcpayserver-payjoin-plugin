using System;

namespace BTCPayServer.Plugins.Payjoin.Data;

internal class PayjoinReceiverInputReservationData
{
    public long Id { get; set; }

    public string InvoiceId { get; set; } = null!;

    public string StoreId { get; set; } = null!;

    public string TransactionId { get; set; } = null!;

    public long OutputIndex { get; set; }

    public DateTimeOffset ReservedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public PayjoinReceiverSessionData Session { get; set; } = null!;
}
