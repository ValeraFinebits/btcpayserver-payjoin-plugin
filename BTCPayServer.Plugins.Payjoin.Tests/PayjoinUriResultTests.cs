using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class PayjoinUriResultTests
{
    private const string PlainBip21 = "bitcoin:bcrt1qexample?amount=0.10000000";

    [Fact]
    public void ActiveIsNotRetryable()
    {
        Assert.False(PayjoinUriResult.Active("bitcoin:bcrt1qexample?pj=https%3A%2F%2Fexample.com%2Fpj").Retryable);
    }

    [Theory]
    [InlineData(PayjoinAvailabilityStatus.TemporarilyUnavailable, true)]
    [InlineData(PayjoinAvailabilityStatus.DisabledByStore, false)]
    [InlineData(PayjoinAvailabilityStatus.MerchantRequirementsUnmet, false)]
    [InlineData(PayjoinAvailabilityStatus.InvoiceNotPayable, false)]
    public void UnavailableDefaultsRetryableFromTheStatus(PayjoinAvailabilityStatus status, bool expected)
    {
        Assert.Equal(expected, PayjoinUriResult.Unavailable(PlainBip21, status, "reason").Retryable);
    }

    [Fact]
    public void TemporaryStatusCanStillDeclareItselfSettled()
    {
        var result = PayjoinUriResult.Unavailable(
            PlainBip21,
            PayjoinAvailabilityStatus.TemporarilyUnavailable,
            PayjoinUnavailableReasons.PayjoinUriMergeLostEndpoint,
            retryable: false);

        Assert.Equal(PayjoinAvailabilityStatus.TemporarilyUnavailable, result.Status);
        Assert.False(result.Retryable);
    }

    [Theory]
    [InlineData(PayjoinReplayedUriVerdict.MergeLostEndpoint, false, false)]
    [InlineData(PayjoinReplayedUriVerdict.MergeLostEndpoint, true, true)]
    [InlineData(PayjoinReplayedUriVerdict.Empty, false, true)]
    [InlineData(PayjoinReplayedUriVerdict.NoPayjoinEndpoint, false, true)]
    internal void VerdictRetryabilityAccountsForAnFfiFault(PayjoinReplayedUriVerdict verdict, bool faulted, bool expectedRetryable)
    {
        Assert.Equal(expectedRetryable, PayjoinUriSessionService.IsRetryableVerdict(verdict, faulted));
    }

    [Fact]
    public void ActiveIsRejectedAsAnUnavailableStatus()
    {
        Assert.Throws<ArgumentException>(() =>
            PayjoinUriResult.Unavailable(PlainBip21, PayjoinAvailabilityStatus.Active, "reason"));
    }

    [Fact]
    public void UnavailableRejectsAMissingReason()
    {
        Assert.Throws<ArgumentException>(() =>
            PayjoinUriResult.Unavailable(PlainBip21, PayjoinAvailabilityStatus.TemporarilyUnavailable, " "));
    }
}
