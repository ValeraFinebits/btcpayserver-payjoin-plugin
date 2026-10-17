using System;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal sealed class PayjoinSessionStaleException : InvalidOperationException
{
    private const string Marker = "PJ_STALE_SESSION";
    public PayjoinSessionStaleException() : base(Marker + ": receiver attempt disappeared or advanced") { }
    public PayjoinSessionStaleException(string message) : base(Marker + ": " + message) { }
    public PayjoinSessionStaleException(string message, Exception innerException) : base(Marker + ": " + message, innerException) { }

    internal static bool IsStale(Exception exception) =>
        exception is PayjoinSessionStaleException || exception.Message.Contains(Marker, StringComparison.Ordinal) ||
        (exception is global::Payjoin.ReceiverReplayException && exception.ToString().Contains(Marker, StringComparison.Ordinal)) ||
        (exception.InnerException is { } inner && IsStale(inner));
}
