using System;

namespace BTCPayServer.Plugins.Payjoin.Services;

/// <summary>
/// The network refused a sender transaction. This is not a reason to end a session: the
/// transaction is signed and valid as far as this plugin can tell, and the node may accept it on
/// a later attempt. Callers leave the session where it is so the next tick tries again.
/// </summary>
public sealed class PayjoinSenderBroadcastException : Exception
{
    /// <summary>
    /// True when retrying cannot help: the inputs are spent or the transaction conflicts with
    /// one the network already holds. Callers end the session through its fallback instead of
    /// retrying for ever.
    /// </summary>
    public bool Permanent { get; }

    // Public because an exception that derives straight from Exception has to be, and this one
    // deliberately does: a broadcast the node refused is its own outcome, not a programming error
    // and not a transport failure, and the callers that retry it must be able to tell it apart.
    public PayjoinSenderBroadcastException(string message) : base(message)
    {
    }

    public PayjoinSenderBroadcastException(string message, bool permanent) : base(message)
    {
        Permanent = permanent;
    }

    public PayjoinSenderBroadcastException()
    {
    }

    public PayjoinSenderBroadcastException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
