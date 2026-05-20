using System;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal static class PayjoinReceiverAccounting
{
    public static long NetReceivedSats(long outputValueSats, long contributedInputsValueSats)
    {
        if (outputValueSats < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputValueSats), outputValueSats, "Output value must be non-negative.");
        }

        if (contributedInputsValueSats < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contributedInputsValueSats), contributedInputsValueSats, "Contributed input value must be non-negative.");
        }

        if (contributedInputsValueSats > outputValueSats)
        {
            throw new ArgumentException(
                $"Contributed input value ({contributedInputsValueSats}) cannot exceed output value ({outputValueSats}).",
                nameof(contributedInputsValueSats));
        }

        return outputValueSats - contributedInputsValueSats;
    }
}
