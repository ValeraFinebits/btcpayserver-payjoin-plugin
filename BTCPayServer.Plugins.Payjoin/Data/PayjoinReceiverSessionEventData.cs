using System;

namespace BTCPayServer.Plugins.Payjoin.Data;

internal class PayjoinReceiverSessionEventData
{
    public long Id { get; set; }

    public string InvoiceId { get; set; } = null!;

    public int Sequence { get; set; }

    public string Event { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }

    public PayjoinReceiverSessionData Session { get; set; } = null!;
}
