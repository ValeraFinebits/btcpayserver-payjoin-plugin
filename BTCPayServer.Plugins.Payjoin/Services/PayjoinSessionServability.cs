using System;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal enum PayjoinPersistedSessionDecision
{
    RebuildEmptyEventLog,
    RebuildAddressMismatch,
    NotServable,
    Reuse
}

internal readonly record struct PayjoinSessionServability(
    bool HasEvents,
    bool IsCloseRequested,
    DateTimeOffset MonitoringExpiresAt,
    string ReceiverAddress)
{
    internal bool IsServable() => HasEvents && !IsCloseRequested && DateTimeOffset.UtcNow < MonitoringExpiresAt;

    // The invoice AMOUNT is deliberately not compared: callers merge pj/pjos onto a BIP21 carrying the
    // current due, so a session built for the full price still serves a partly paid invoice.
    internal bool MatchesInvoice(string destination) =>
        string.Equals(ReceiverAddress, destination, StringComparison.Ordinal);

    internal PayjoinPersistedSessionDecision Decide(string destination)
    {
        if (!HasEvents)
        {
            return PayjoinPersistedSessionDecision.RebuildEmptyEventLog;
        }

        if (!IsServable())
        {
            return PayjoinPersistedSessionDecision.NotServable;
        }

        return MatchesInvoice(destination)
            ? PayjoinPersistedSessionDecision.Reuse
            : PayjoinPersistedSessionDecision.RebuildAddressMismatch;
    }
}
