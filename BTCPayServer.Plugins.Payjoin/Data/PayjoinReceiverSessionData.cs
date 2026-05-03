using BTCPayServer.Client.Models;
using System;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.Payjoin.Data;

internal class PayjoinReceiverSessionData
{
    public string InvoiceId { get; set; } = null!;

    public string StoreId { get; set; } = null!;

    public string ReceiverAddress { get; set; } = null!;

    public string OhttpRelayUrl { get; set; } = null!;

    public DateTimeOffset MonitoringExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsCloseRequested { get; set; }

    public InvoiceStatus? CloseInvoiceStatus { get; set; }

    public DateTimeOffset? CloseRequestedAt { get; set; }

    public bool InitializedPollAfterCloseRequestConsumed { get; set; }

    public string? ContributedInputTransactionId { get; set; }

    public long? ContributedInputOutputIndex { get; set; }

    public ICollection<PayjoinReceiverSessionEventData> Events { get; } = [];
}
