using System;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal sealed class PayjoinReceiverRelayTimeoutException : TaskCanceledException
{
    public PayjoinReceiverRelayTimeoutException()
    {
    }

    public PayjoinReceiverRelayTimeoutException(string? message)
        : base(message)
    {
    }

    public PayjoinReceiverRelayTimeoutException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public PayjoinReceiverRelayTimeoutException(TimeSpan timeout, OperationCanceledException innerException)
        : base($"Payjoin receiver relay request timed out after {timeout.TotalSeconds:0.###} seconds.", innerException)
    {
        Timeout = timeout;
    }

    public TimeSpan Timeout { get; }
}
