using System;

namespace BTCPayServer.Plugins.Payjoin.Services;

/// <summary>
/// A live session already pays the same URI. The read-side check catches this in the common case;
/// this exception is the database's answer when two writers pass that check together, raised off
/// the unique live-Bip21 index. The caller treats it exactly like the read-side refusal.
/// </summary>
public sealed class PayjoinSenderDuplicateSessionException : Exception
{
    // Public because an exception that derives straight from Exception has to be.
    public PayjoinSenderDuplicateSessionException(string message) : base(message)
    {
    }

    public PayjoinSenderDuplicateSessionException()
    {
    }

    public PayjoinSenderDuplicateSessionException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
