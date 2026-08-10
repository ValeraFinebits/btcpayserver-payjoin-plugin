using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class PayjoinInvoicePaymentUrlServiceTests
{
    private const string FallbackPaymentUrl = "bitcoin:bcrt1qfallback?amount=0.10000000";

    private static string CreatePayjoinUri()
    {
        using var key = new Key();
        var address = key.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        return $"bitcoin:{address}?amount=0.10000000&pjos=0&pj=https%3A%2F%2Fexample.com%2Fpj";
    }

    private static string CreatePlainUri()
    {
        using var key = new Key();
        var address = key.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        return $"bitcoin:{address}?amount=0.10000000";
    }

    [Fact]
    public async Task GetInvoicePaymentUrlAsyncThrowsWhenInvoiceIdMissing()
    {
        var service = new PayjoinInvoicePaymentUrlService(null!, null!, null!, null!);

        await Assert.ThrowsAsync<ArgumentException>(() => service.GetInvoicePaymentUrlAsync(" ", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ActiveResultKeepsPayjoinUriAndReason()
    {
        var payjoinUri = CreatePayjoinUri();

        var response = PayjoinInvoicePaymentUrlService.CreateResponse(PayjoinUriResult.Active(payjoinUri));

        Assert.Equal(PayjoinAvailabilityStatus.Active, response.Status);
        Assert.Equal(payjoinUri, response.Bip21);
        Assert.Null(response.UnavailableReason);
    }

    [Fact]
    public void FallbackResultKeepsStatusAndReasonFromBuilder()
    {
        var result = PayjoinUriResult.Unavailable(FallbackPaymentUrl, PayjoinAvailabilityStatus.DisabledByStore, "payjoin is disabled by store settings");

        var response = PayjoinInvoicePaymentUrlService.CreateResponse(result);

        Assert.Equal(PayjoinAvailabilityStatus.DisabledByStore, response.Status);
        Assert.Equal("payjoin is disabled by store settings", response.UnavailableReason);
        Assert.Equal(FallbackPaymentUrl, response.Bip21);
    }

    [Fact]
    public void PlainUriHasNoSupportedPayjoinEndpoint()
    {
        Assert.False(PayjoinUriSessionService.HasSupportedPayjoinEndpoint(CreatePlainUri()));
    }

    [Fact]
    public void UnparseableUriHasNoSupportedPayjoinEndpoint()
    {
        Assert.False(PayjoinUriSessionService.HasSupportedPayjoinEndpoint("bitcoin:bcrt1qexample?amount=0.10000000"));
    }

    [Fact]
    public void PayjoinUriHasSupportedPayjoinEndpoint()
    {
        Assert.True(PayjoinUriSessionService.HasSupportedPayjoinEndpoint(CreatePayjoinUri()));
    }

    [Fact]
    public void UnavailableResultRejectsMissingReason()
    {
        Assert.Throws<ArgumentException>(() =>
            PayjoinUriResult.Unavailable(FallbackPaymentUrl, PayjoinAvailabilityStatus.TemporarilyUnavailable, " "));
    }

    [Fact]
    public void UnavailableResultRejectsActiveStatus()
    {
        Assert.Throws<ArgumentException>(() =>
            PayjoinUriResult.Unavailable(FallbackPaymentUrl, PayjoinAvailabilityStatus.Active, "reason"));
    }

    [Fact]
    public void ActiveResultCarriesNoReason()
    {
        Assert.Null(PayjoinUriResult.Active(CreatePayjoinUri()).Reason);
    }
}
