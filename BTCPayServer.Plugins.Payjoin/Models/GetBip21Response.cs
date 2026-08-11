using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace BTCPayServer.Plugins.Payjoin.Models;

public sealed class GetBip21Response
{
    public required string Bip21 { get; init; }

    [JsonConverter(typeof(StringEnumConverter))]
    public required PayjoinAvailabilityStatus Status { get; init; }

    public string? UnavailableReason { get; init; }

    public bool Retryable { get; init; }
}
