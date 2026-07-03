using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Plugins.Payjoin.Services;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinFallbackBroadcasterTests
{
    [Fact]
    public void ShouldAttemptBroadcastOnlyForUnpaidInvoicesWithAnUnreconciledFallback()
    {
        Assert.True(PayjoinFallbackBroadcaster.ShouldAttemptBroadcast(InvoiceStatus.New, PayjoinAccountingBridgeStatus.PendingFinalTransaction, hasFallback: true));
        Assert.True(PayjoinFallbackBroadcaster.ShouldAttemptBroadcast(InvoiceStatus.Expired, PayjoinAccountingBridgeStatus.PendingFallback, hasFallback: true));
        Assert.True(PayjoinFallbackBroadcaster.ShouldAttemptBroadcast(InvoiceStatus.New, PayjoinAccountingBridgeStatus.Expired, hasFallback: true));

        Assert.False(PayjoinFallbackBroadcaster.ShouldAttemptBroadcast(InvoiceStatus.New, PayjoinAccountingBridgeStatus.Reconciled, hasFallback: true));
        Assert.False(PayjoinFallbackBroadcaster.ShouldAttemptBroadcast(InvoiceStatus.Settled, PayjoinAccountingBridgeStatus.PendingFinalTransaction, hasFallback: true));
        Assert.False(PayjoinFallbackBroadcaster.ShouldAttemptBroadcast(InvoiceStatus.Processing, PayjoinAccountingBridgeStatus.PendingFinalTransaction, hasFallback: true));
        Assert.False(PayjoinFallbackBroadcaster.ShouldAttemptBroadcast(InvoiceStatus.Invalid, PayjoinAccountingBridgeStatus.PendingFinalTransaction, hasFallback: true));
        Assert.False(PayjoinFallbackBroadcaster.ShouldAttemptBroadcast(InvoiceStatus.New, PayjoinAccountingBridgeStatus.PendingFinalTransaction, hasFallback: false));
        Assert.False(PayjoinFallbackBroadcaster.ShouldAttemptBroadcast(null, PayjoinAccountingBridgeStatus.PendingFinalTransaction, hasFallback: true));
    }
}
