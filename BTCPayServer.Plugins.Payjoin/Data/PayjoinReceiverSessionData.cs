using BTCPayServer.Client.Models;
using System;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.Payjoin.Data;

internal class PayjoinReceiverSessionData
{
    public string InvoiceId { get; set; } = null!;

    public Guid SessionInstanceId { get; init; } = Guid.NewGuid();

    public string StoreId { get; set; } = null!;

    public string ReceiverAddress { get; set; } = null!;

    public DateTimeOffset MonitoringExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsCloseRequested { get; set; }

    public InvoiceStatus? CloseInvoiceStatus { get; set; }

    public DateTimeOffset? CloseRequestedAt { get; set; }

    public bool InitializedPollAfterCloseRequestConsumed { get; set; }

    public string? ContributedInputTransactionId { get; set; }

    public long? ContributedInputOutputIndex { get; set; }

    public string? PayjoinUri { get; set; }

    // Version within one immutable SessionInstanceId. Every event append, input reservation and
    // bridge write advances it, atomically with the related data. Early rejection must invalidate
    // its checked snapshot if another writer advances negotiation or prepares a proposal.
    // The instance token also fences stale entities/persisters after delete-and-recreate (ABA).
    // Close/consume/cache writes still compare both tokens but need not advance this version.
    // The unique (InvoiceId, Sequence) index additionally orders events. Contract:
    // PayjoinReceiverSessionStoreRelationalTests.DestructiveAndProtectingWritesAdvanceTheAttemptVersion.
    public int AttemptVersion { get; set; }

    public ICollection<PayjoinReceiverSessionEventData> Events { get; } = [];

    public ICollection<PayjoinReceiverInputReservationData> InputReservations { get; } = [];
}
