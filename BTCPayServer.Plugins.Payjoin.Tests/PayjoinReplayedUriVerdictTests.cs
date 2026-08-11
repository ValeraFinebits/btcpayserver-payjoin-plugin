using BTCPayServer.Plugins.Payjoin.Services;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class PayjoinReplayedUriVerdictTests
{
    private const string InvoiceBip21 = "bitcoin:bcrt1qexample?amount=0.10000000&lightning=lnbcrt123";

    private static string CreateSessionUri()
    {
        using var key = new Key();
        var address = key.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        return $"bitcoin:{address}?amount=0.10000000&pjos=0&pj=https%3A%2F%2Fexample.com%2Fpj";
    }

    private static string CreateInvoiceBip21()
    {
        using var key = new Key();
        var address = key.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        return $"bitcoin:{address}?amount=0.06000000";
    }

    [Fact]
    public void UriCarryingAnEndpointIsServableAndMergesOntoTheInvoice()
    {
        var invoiceBip21 = CreateInvoiceBip21();

        var verdict = PayjoinBip21.JudgeReplayedUri(CreateSessionUri(), invoiceBip21, out var merged, out var fault);

        Assert.Equal(PayjoinReplayedUriVerdict.Servable, verdict);
        Assert.Null(fault);
        Assert.Contains("amount=0.06000000", merged, StringComparison.Ordinal);
        Assert.Contains("pj=https%3A%2F%2Fexample.com%2Fpj", merged, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplayProducingNothingIsEmpty()
    {
        Assert.All(new[] { null, string.Empty, "   " }, replayed =>
        {
            var verdict = PayjoinBip21.JudgeReplayedUri(replayed, InvoiceBip21, out var merged, out var fault);

            Assert.Equal(PayjoinReplayedUriVerdict.Empty, verdict);
            Assert.Null(fault);
            Assert.Equal(InvoiceBip21, merged);
        });
    }

    [Fact]
    public void MissingInvoiceBip21ThrowsRatherThanServingTheSessionUri()
    {
        var sessionUri = CreateSessionUri();

        Assert.All(new[] { null, string.Empty, "   " }, invoiceBip21 =>
            Assert.ThrowsAny<ArgumentException>(() =>
                PayjoinBip21.JudgeReplayedUri(sessionUri, invoiceBip21!, out _, out _)));
    }

    [Theory]
    [InlineData(PayjoinReplayedUriVerdict.Empty, false)]
    [InlineData(PayjoinReplayedUriVerdict.NoPayjoinEndpoint, false)]
    [InlineData(PayjoinReplayedUriVerdict.MergeLostEndpoint, true)]
    internal void VerdictsThatBlameTheInvoiceAreTheOnesKeptAndNotRetried(PayjoinReplayedUriVerdict verdict, bool expected)
    {
        Assert.Equal(expected, PayjoinUriSessionService.IndictsTheInvoice(verdict));
    }

    [Fact]
    public void EveryUnusableVerdictHasAReason()
    {
        Assert.All(
            Enum.GetValues<PayjoinReplayedUriVerdict>().Where(v => v != PayjoinReplayedUriVerdict.Servable),
            verdict => Assert.False(string.IsNullOrWhiteSpace(PayjoinUriSessionService.ReasonFor(verdict))));
    }

    [Fact]
    public void UriWithoutAPayjoinEndpointIsRejected()
    {
        using var key = new Key();
        var address = key.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);

        var verdict = PayjoinBip21.JudgeReplayedUri($"bitcoin:{address}?amount=0.1", InvoiceBip21, out var merged, out var fault);

        Assert.Equal(PayjoinReplayedUriVerdict.NoPayjoinEndpoint, verdict);
        Assert.Equal(InvoiceBip21, merged);
        Assert.Null(fault);
    }

    [Fact]
    public void MergeOntoAnUnparseableInvoiceBip21IsRejected()
    {
        var verdict = PayjoinBip21.JudgeReplayedUri(CreateSessionUri(), "bitcoin:not-an-address?amount=0.1", out var merged, out var fault);

        Assert.Equal(PayjoinReplayedUriVerdict.MergeLostEndpoint, verdict);
        Assert.Equal("bitcoin:not-an-address?amount=0.1", merged);
        Assert.Null(fault);
    }
}
