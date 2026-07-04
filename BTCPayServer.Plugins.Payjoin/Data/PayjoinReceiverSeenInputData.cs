using System;

namespace BTCPayServer.Plugins.Payjoin.Data;

internal class PayjoinReceiverSeenInputData
{
    public long Id { get; set; }

    public string TransactionId { get; set; } = null!;

    public long OutputIndex { get; set; }

    public DateTimeOffset SeenAt { get; set; }
}
