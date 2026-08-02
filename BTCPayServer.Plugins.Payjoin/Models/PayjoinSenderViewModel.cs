using System;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.Payjoin.Models;

public class PayjoinSenderViewModel
{
    public string? Bip21 { get; set; }

    public decimal? FeeRateSatPerVb { get; set; }

    public IReadOnlyList<PayjoinSenderSessionViewModel> Sessions { get; set; } = [];
}

public class PayjoinSenderSessionViewModel
{
    public required string SenderSessionId { get; init; }

    public required string DestinationAddress { get; init; }

    public required long AmountSats { get; init; }

    public required string Status { get; init; }

    public string? BroadcastTransactionId { get; init; }

    public string? FailureMessage { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}
