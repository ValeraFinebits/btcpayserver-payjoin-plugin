using System;

namespace BTCPayServer.Plugins.Payjoin.Services;

public sealed class PayjoinStoreSettingsUnavailableException : Exception
{
    public PayjoinStoreSettingsUnavailableException()
        : this("Payjoin store settings could not be read.")
    {
    }

    public PayjoinStoreSettingsUnavailableException(string message)
        : base(message)
    {
    }

    public PayjoinStoreSettingsUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
