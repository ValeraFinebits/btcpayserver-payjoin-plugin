using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;

namespace BTCPayServer.Plugins.Payjoin.Models;

public sealed class PayjoinCheckoutAvailabilityResponse
{
    [JsonConverter(typeof(StringEnumConverter))]
    public required PayjoinCheckoutAvailabilityStatus Status { get; init; }

    public required bool Retryable { get; init; }

    public static PayjoinCheckoutAvailabilityResponse From(GetBip21Response paymentUrl)
    {
        ArgumentNullException.ThrowIfNull(paymentUrl);

        return new PayjoinCheckoutAvailabilityResponse
        {
            Status = paymentUrl.Status == PayjoinAvailabilityStatus.Active
                ? PayjoinCheckoutAvailabilityStatus.Active
                : PayjoinCheckoutAvailabilityStatus.Unavailable,
            Retryable = paymentUrl.Retryable
        };
    }
}
