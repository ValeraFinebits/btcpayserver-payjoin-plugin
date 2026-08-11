using BTCPayServer.Plugins.Payjoin.Models;
using System;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal sealed class PayjoinUriResult
{
    private PayjoinUriResult(string paymentUrl, PayjoinAvailabilityStatus status, string? reason, bool retryable)
    {
        PaymentUrl = paymentUrl;
        Status = status;
        Reason = reason;
        Retryable = retryable;
    }

    public string PaymentUrl { get; }

    public PayjoinAvailabilityStatus Status { get; }

    public string? Reason { get; }

    public bool Retryable { get; }

    public static PayjoinUriResult Active(string payjoinUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payjoinUri);

        return new PayjoinUriResult(payjoinUri, PayjoinAvailabilityStatus.Active, null, retryable: false);
    }

    public static PayjoinUriResult Unavailable(
        string plainBip21,
        PayjoinAvailabilityStatus status,
        string reason,
        bool? retryable = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plainBip21);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (status == PayjoinAvailabilityStatus.Active)
        {
            throw new ArgumentException("Active is not an unavailable status.", nameof(status));
        }

        return new PayjoinUriResult(
            plainBip21,
            status,
            reason,
            retryable ?? status == PayjoinAvailabilityStatus.TemporarilyUnavailable);
    }
}
