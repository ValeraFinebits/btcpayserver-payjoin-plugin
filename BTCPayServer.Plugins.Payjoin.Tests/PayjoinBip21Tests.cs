using BTCPayServer.Plugins.Payjoin.Services;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class PayjoinBip21Tests
{
    private static string CreateAddress()
    {
        using var key = new Key();
        return key.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest).ToString();
    }

    private static string CreatePayjoinUri() =>
        $"bitcoin:{CreateAddress()}?amount=0.10000000&pjos=0&pj=https%3A%2F%2Fexample.com%2Fpj";

    private static string CreatePlainUri() =>
        $"bitcoin:{CreateAddress()}?amount=0.10000000";

    [Fact]
    public void WhollyUppercasedUriIsRejectedByTheParser()
    {
        var payjoinUri = CreatePayjoinUri();

        Assert.True(PayjoinBip21.HasSupportedPayjoinEndpoint(payjoinUri));
        Assert.False(PayjoinBip21.HasSupportedPayjoinEndpoint(payjoinUri.ToUpperInvariant()));
    }

    [Fact]
    public void QrFormBtcpayActuallyRendersIsAcceptedByTheParser()
    {
        var address = CreateAddress();
        var plainQr = $"bitcoin:{address.ToUpperInvariant()}?amount=0.10000000&lightning=LNBCRT123";
        var payjoinUri = $"bitcoin:{address}?amount=0.1&pjos=0&pj=https%3A%2F%2Fexample.com%2Fpj";

        var mergedQr = PayjoinBip21.MergePayjoinIntoPaymentUrl(plainQr, payjoinUri);

        Assert.Contains($"bitcoin:{address.ToUpperInvariant()}?", mergedQr, StringComparison.Ordinal);
        Assert.Contains("&lightning=LNBCRT123", mergedQr, StringComparison.Ordinal);
        Assert.Contains("pjos=0", mergedQr, StringComparison.Ordinal);
        Assert.Contains("pj=https", mergedQr, StringComparison.Ordinal);

        Assert.True(PayjoinBip21.HasSupportedPayjoinEndpoint(mergedQr));
    }

    [Fact]
    public void PlainUriHasNoSupportedPayjoinEndpoint()
    {
        Assert.False(PayjoinBip21.HasSupportedPayjoinEndpoint(CreatePlainUri()));
    }

    [Fact]
    public void UnparseableUriHasNoSupportedPayjoinEndpoint()
    {
        Assert.False(PayjoinBip21.HasSupportedPayjoinEndpoint("bitcoin:bcrt1qexample?amount=0.10000000"));
    }

    [Fact]
    public void MergePayjoinIntoPaymentUrlPreservesExistingQueryParameters()
    {
        const string baseUrl = "bitcoin:bcrt1qexample?amount=0.10000000&lightning=lnbcrt123";
        const string payjoinUrl = "bitcoin:bcrt1qexample?amount=0.10000000&pj=https%3A%2F%2Fexample.com%2Fpj";

        var merged = PayjoinBip21.MergePayjoinIntoPaymentUrl(baseUrl, payjoinUrl);

        Assert.Equal(
            "bitcoin:bcrt1qexample?amount=0.10000000&pj=https%3A%2F%2Fexample.com%2Fpj&lightning=lnbcrt123",
            merged);
    }

    [Fact]
    public void MergePayjoinIntoPaymentUrlPreservesOutputSubstitutionParameter()
    {
        const string baseUrl = "bitcoin:bcrt1qexample?amount=0.10000000";
        const string payjoinUrl = "bitcoin:bcrt1qexample?amount=0.10000000&pjos=0&pj=https%3A%2F%2Fexample.com%2Fpj";

        var merged = PayjoinBip21.MergePayjoinIntoPaymentUrl(baseUrl, payjoinUrl);

        Assert.Equal(
            "bitcoin:bcrt1qexample?amount=0.10000000&pjos=0&pj=https%3A%2F%2Fexample.com%2Fpj",
            merged);
    }

    [Fact]
    public void MergePayjoinIntoPaymentUrlInsertsPayjoinParametersBeforeLightningFallback()
    {
        const string baseUrl = "bitcoin:bcrt1qexample?amount=0.10000000&lightning=lnbcrt123";
        const string payjoinUrl = "bitcoin:bcrt1qexample?amount=0.10000000&pjos=0&pj=https%3A%2F%2Fexample.com%2Fpj";

        var merged = PayjoinBip21.MergePayjoinIntoPaymentUrl(baseUrl, payjoinUrl);

        Assert.Equal(
            "bitcoin:bcrt1qexample?amount=0.10000000&pjos=0&pj=https%3A%2F%2Fexample.com%2Fpj&lightning=lnbcrt123",
            merged);
    }

    [Fact]
    public void MergePayjoinIntoPaymentUrlRemovesStalePayjoinParametersWhenFallingBackToPlainBip21()
    {
        const string baseUrl = "bitcoin:BCRT1QEXAMPLE?amount=0.10000000&lightning=LNBCRT123&pjos=0&pj=https%3A%2F%2Fold.example%2Fpj";
        const string payjoinFallbackUrl = "bitcoin:bcrt1qexample?amount=0.10000000";

        var merged = PayjoinBip21.MergePayjoinIntoPaymentUrl(baseUrl, payjoinFallbackUrl);

        Assert.Equal("bitcoin:BCRT1QEXAMPLE?amount=0.10000000&lightning=LNBCRT123", merged);
    }

    [Fact]
    public void ReplacePayjoinQueryParametersDropsBothPayjoinKeys()
    {
        const string url = "bitcoin:bcrt1qexample?amount=0.10000000&PJOS=0&PJ=https%3A%2F%2Fexample.com%2Fpj&lightning=lnbcrt123";

        var replaced = PayjoinBip21.ReplacePayjoinQueryParameters(url, []);

        Assert.Equal("bitcoin:bcrt1qexample?amount=0.10000000&lightning=lnbcrt123", replaced);
    }

    [Fact]
    public void ReplacePayjoinQueryParametersKeepsANonPayjoinUrlIntact()
    {
        const string url = "bitcoin:bcrt1qexample?amount=0.10000000";

        Assert.Equal(url, PayjoinBip21.ReplacePayjoinQueryParameters(url, []));
    }

    [Fact]
    public void ReplacePayjoinQueryParametersDropsTheQuerySeparatorWhenNothingRemains()
    {
        const string url = "bitcoin:bcrt1qexample?pjos=0&pj=https%3A%2F%2Fexample.com%2Fpj";

        Assert.Equal("bitcoin:bcrt1qexample", PayjoinBip21.ReplacePayjoinQueryParameters(url, []));
    }
}
