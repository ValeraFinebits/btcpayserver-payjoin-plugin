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

    // The txid of the original transaction. It doubles as the fallback the sender
    // broadcasts when the payjoin does not complete, and as the dedup handle against
    // double-paying the same URI. The unsigned txid is stable for the segwit inputs
    // this plugin sends, so it is known before signing. TODO: index sessions on the
    // receiver's ephemeral pubkey once the bindings expose PjParam.receiver_pubkey().
    public string OriginalTransactionId { get; set; } = null!;

    // Set while a wallet that cannot sign on the server works through BTCPay's own
    // pending-transaction screen. It names the row this session waits on, and it is
    // reused for the second signing round once the receiver returns a proposal.
    public string? PendingTransactionId { get; set; }

    // The fee rate the operator chose, in sat/kWU, which is the unit rust-payjoin wants. It is
    // the floor the receiver's proposal must clear, and it sizes the fee this sender contributes
    // for the receiver's extra input, so the second signing round needs it as well as the first.
    public long FeeRateSatPerKwu { get; set; }

    // The outpoints the original transaction spends. A live session holds these coins even when
    // no pending transaction does, so the next transaction this store builds must not take them.
    public string[] OutpointsUsed { get; set; } = [];

    // The signed original, as raw transaction hex. It is the fallback: the payment this session
    // makes if the payjoin does not happen. rust-payjoin holds a copy, but only while the session
    // is open, and it closes the session as soon as it hands over a proposal. Keeping our own
    // copy means the operator can always fall back to the plain payment.
    public string? OriginalTransactionHex { get; set; }

    // The base URL of the request that started this session. A background poller has no
    // HttpContext, so it cannot derive one, and the second signing round still needs to create
    // a pending transaction. BTCPay stores the same thing on the pending transaction itself.
    public string? RequestBaseUrl { get; set; }

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
    Failed,

    // A wallet that cannot sign on the server holds the transaction. Nothing reaches
    // the directory until the operator signs through BTCPay's pending-transaction
    // screen, so the poller skips these. Appended rather than inserted because the
    // status persists as an integer, and renumbering would rewrite existing rows.
    AwaitingSignature
}
