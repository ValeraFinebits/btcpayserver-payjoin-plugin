using BTCPayServer.Plugins.Payjoin.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class PayjoinCheckoutAvailabilityResponseTests
{
    private static JObject Serialize(PayjoinAvailabilityStatus status, bool retryable = false) =>
        JObject.Parse(JsonConvert.SerializeObject(PayjoinCheckoutAvailabilityResponse.From(new GetBip21Response
        {
            Bip21 = "bitcoin:bcrt1qexample?amount=0.10000000",
            Status = status,
            UnavailableReason = status == PayjoinAvailabilityStatus.Active ? null : "reason",
            Retryable = retryable
        })));

    [Fact]
    public void SerializesExactlyStatusAndRetryable()
    {
        Assert.All(Enum.GetValues<PayjoinAvailabilityStatus>(), status =>
            Assert.Equal(
                new[] { "Status", "Retryable" },
                Serialize(status).Properties().Select(property => property.Name)));
    }

    [Theory]
    [InlineData(PayjoinAvailabilityStatus.Active, "Active")]
    [InlineData(PayjoinAvailabilityStatus.TemporarilyUnavailable, "Unavailable")]
    [InlineData(PayjoinAvailabilityStatus.DisabledByStore, "Unavailable")]
    [InlineData(PayjoinAvailabilityStatus.MerchantRequirementsUnmet, "Unavailable")]
    [InlineData(PayjoinAvailabilityStatus.InvoiceNotPayable, "Unavailable")]
    public void CollapsesEveryStatusToAPascalCaseName(PayjoinAvailabilityStatus status, string expected)
    {
        Assert.Equal(expected, Serialize(status)["Status"]!.Value<string>());
    }

    [Theory]
    [InlineData(PayjoinAvailabilityStatus.TemporarilyUnavailable, true)]
    [InlineData(PayjoinAvailabilityStatus.TemporarilyUnavailable, false)]
    [InlineData(PayjoinAvailabilityStatus.MerchantRequirementsUnmet, false)]
    [InlineData(PayjoinAvailabilityStatus.InvoiceNotPayable, true)]
    public void CarriesRetryableThroughUnchanged(PayjoinAvailabilityStatus status, bool retryable)
    {
        Assert.Equal(retryable, Serialize(status, retryable)["Retryable"]!.Value<bool>());
    }
}
