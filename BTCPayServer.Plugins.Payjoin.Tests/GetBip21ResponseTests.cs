using BTCPayServer.Plugins.Payjoin.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class GetBip21ResponseTests
{
    [Fact]
    public void SerializesEveryStatusAsItsOwnPascalCaseName()
    {
        Assert.All(Enum.GetValues<PayjoinAvailabilityStatus>(), status =>
        {
            var serialized = JObject.Parse(JsonConvert.SerializeObject(new GetBip21Response
            {
                Bip21 = "bitcoin:bcrt1qexample?amount=0.10000000",
                Status = status,
                Retryable = false
            }));

            Assert.Equal(status.ToString(), serialized["Status"]!.Value<string>());
        });
    }

    [Fact]
    public void DoesNotSerializeRemovedPayjoinEnabledFlag()
    {
        var serialized = JObject.Parse(JsonConvert.SerializeObject(new GetBip21Response
        {
            Bip21 = "bitcoin:bcrt1qexample?amount=0.10000000",
            Status = PayjoinAvailabilityStatus.Active,
            Retryable = false
        }));

        Assert.DoesNotContain(
            serialized.Properties(),
            property => property.Name.Equals("PayjoinEnabled", StringComparison.OrdinalIgnoreCase));
    }
}
