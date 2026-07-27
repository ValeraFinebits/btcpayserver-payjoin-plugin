using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace BTCPayServer.Plugins.Payjoin.Models;

public sealed class GetCheckoutBip21Response
{
    public required string Bip21 { get; init; }

    [JsonConverter(typeof(StringEnumConverter))]
    public required PayjoinCheckoutAvailabilityStatus Status { get; init; }
}
