using BTCPayServer.Plugins.Payjoin.Services;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class PayjoinUriSessionServiceAmountTests
{
    [Fact]
    public void ExpectedAmountMatchesTheInvoiceDue()
    {
        Assert.True(PayjoinUriSessionService.HasExpectedAmount(10_000_000UL, 0.1m));
    }

    [Fact]
    public void ExpectedAmountRejectsASessionArmedForMoreThanIsStillDue()
    {
        Assert.False(PayjoinUriSessionService.HasExpectedAmount(10_000_000UL, 0.04m));
    }

    [Fact]
    public void ExpectedAmountRejectsASessionCarryingNoAmount()
    {
        Assert.False(PayjoinUriSessionService.HasExpectedAmount(null, 0.1m));
    }

    [Fact]
    public void ExpectedAmountRejectsAnAmountBeyondLongRange()
    {
        Assert.False(PayjoinUriSessionService.HasExpectedAmount(ulong.MaxValue, 0.1m));
    }
}
