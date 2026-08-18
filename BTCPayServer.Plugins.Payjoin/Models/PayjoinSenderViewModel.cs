using System;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.Payjoin.Models;

public class PayjoinSenderViewModel
{
    public string? StoreId { get; set; }

    // The wallet these payments spend from, in the form core's route binder accepts. Building it
    // by hand is what broke both wallet links: the binder needs the "S-" prefix.
    public string? WalletId { get; set; }

    public IReadOnlyList<PayjoinSenderSessionViewModel> Sessions { get; set; } = [];
}

public class PayjoinSenderSessionViewModel
{
    public required string SenderSessionId { get; init; }

    public required string DestinationAddress { get; init; }

    public required long AmountSats { get; init; }

    public required string Status { get; init; }

    // Set while the session waits for a signature from a wallet that cannot sign on the server.
    // The view links it to BTCPay's own pending-transaction screen.
    public string? PendingTransactionId { get; init; }

    // A live session can be stopped. Stopping does not stop the payment: whenever the original
    // is signed, it goes out as a plain transaction instead of a payjoin.
    public required bool CanCancel { get; init; }

    public string? BroadcastTransactionId { get; init; }

    public string? FailureMessage { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}
