using System;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal static class PayjoinExceptionCompatibility
{
    public static bool IsParseException(Exception exception)
    {
        var fullName = exception.GetType().FullName;
        return fullName is not null && (
            fullName.EndsWith("PjParseException", StringComparison.Ordinal) ||
            fullName.EndsWith("UriParseException", StringComparison.Ordinal) ||
            fullName.EndsWith("UriParseError", StringComparison.Ordinal));
    }

    public static bool IsUnsupportedException(Exception exception)
    {
        return exception.GetType().FullName?.EndsWith("PjNotSupported", StringComparison.Ordinal) == true;
    }
}
