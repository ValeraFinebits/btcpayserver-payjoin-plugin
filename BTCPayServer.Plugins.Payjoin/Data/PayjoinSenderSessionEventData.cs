using System;

namespace BTCPayServer.Plugins.Payjoin.Data;

internal class PayjoinSenderSessionEventData
{
    public long Id { get; set; }

    public string SenderSessionId { get; set; } = null!;

    public int Sequence { get; set; }

    public string Event { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }

    public PayjoinSenderSessionData Session { get; set; } = null!;
}
