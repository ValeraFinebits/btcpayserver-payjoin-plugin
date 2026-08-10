using BTCPayServer.Plugins.Payjoin.Models;
using System;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal sealed class PayjoinUriResult
{
    private PayjoinUriResult(string paymentUrl, PayjoinAvailabilityStatus status, string? reason)
    {
        PaymentUrl = paymentUrl;
        Status = status;
        Reason = reason;
    }

    public string PaymentUrl { get; }

    public PayjoinAvailabilityStatus Status { get; }

    public string? Reason { get; }

    public static PayjoinUriResult Active(string payjoinUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payjoinUri);

        return new PayjoinUriResult(payjoinUri, PayjoinAvailabilityStatus.Active, null);
    }

    public static PayjoinUriResult Unavailable(string plainBip21, PayjoinAvailabilityStatus status, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plainBip21);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (status == PayjoinAvailabilityStatus.Active)
        {
            throw new ArgumentException("Active is not an unavailable status.", nameof(status));
        }

        return new PayjoinUriResult(plainBip21, status, reason);
    }
}
