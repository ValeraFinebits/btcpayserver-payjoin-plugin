using System;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.Payjoin.Data;

internal class PayjoinSenderSessionData
{
    public string SenderSessionId { get; set; } = null!;

    public string StoreId { get; set; } = null!;

    // The full BIP 21 URI this session pays, kept for display and for replaying the
    // payjoin endpoint parameters.
    public string Bip21 { get; set; } = null!;

    public string DestinationAddress { get; set; } = null!;

    public long AmountSats { get; set; }

    // The txid of the signed original transaction. It doubles as the fallback the
    // sender broadcasts when the payjoin does not complete, and as the dedup handle
    // against double-paying the same URI. TODO: index sessions on the receiver's
    // ephemeral pubkey once the bindings expose PjParam.receiver_pubkey().
    public string OriginalTransactionId { get; set; } = null!;

    // Set when a transaction reaches the network: the payjoin txid when the proposal
    // completed, or the original txid when the fallback was broadcast.
    public string? BroadcastTransactionId { get; set; }

    public PayjoinSenderSessionStatus Status { get; set; }

    public string? FailureMessage { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<PayjoinSenderSessionEventData> Events { get; } = [];
}

internal enum PayjoinSenderSessionStatus
{
    // The session still posts or polls through the directory.
    Pending,
    // The receiver's proposal was signed and broadcast.
    CompletedPayjoin,
    // The original transaction was broadcast instead of a payjoin.
    CompletedFallback,
    // The session ended without any broadcast; the failure message says why.
    Failed
}
