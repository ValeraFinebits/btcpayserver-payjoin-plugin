using BTCPayServer.Client.Models;
using System;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.Payjoin.Data;

internal class PayjoinReceiverSessionData
{
    public string InvoiceId { get; set; } = null!;

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

    // Concurrency token, deliberately PARTIAL. Only destructive writes bump it:
    // - RemoveAllSessionEvents (event-log wipe, which also clears PayjoinUri),
    // - ReserveContributedInputCore (claiming the contributed input).
    // It guards the discard-vs-reserve and wipe-vs-cache races. All other session writes
    // (close/consume flags, the cached URI, cleanup, event appends) intentionally do NOT bump
    // it: they are idempotent last-write-wins updates, and event-log ordering is enforced by
    // the unique (InvoiceId, Sequence) index instead. Do not use this value to detect
    // event-log changes. The contract is pinned by
    // PayjoinReceiverSessionStoreRelationalTests.OnlyDestructiveWritesAdvanceTheDestructiveWriteStamp.
    public int DestructiveWriteStamp { get; set; }

    public ICollection<PayjoinReceiverSessionEventData> Events { get; } = [];

    public ICollection<PayjoinReceiverInputReservationData> InputReservations { get; } = [];
}
