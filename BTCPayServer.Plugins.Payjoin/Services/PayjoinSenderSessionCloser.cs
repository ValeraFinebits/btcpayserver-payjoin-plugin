using Payjoin;
using System;

namespace BTCPayServer.Plugins.Payjoin.Services;

/// <summary>
/// Records in the library's event log that a session has been handed over, so a later replay sees
/// a finished session rather than one still waiting on the directory.
///
/// The library closes a sender session itself as soon as it produces a valid proposal, so this
/// often has nothing to do. It matters when the plugin ends a session first: the operator stops
/// it, or the transaction it waits on will never be signed.
/// </summary>
internal static class PayjoinSenderSessionCloser
{
    /// <summary>
    /// Returns false when the log could not be advanced. A payment that already reached the
    /// network must not be reported as a failure over a bookkeeping step, so callers log and
    /// carry on.
    /// </summary>
    internal static bool TryClose(JsonSenderSessionPersister persister)
    {
        ArgumentNullException.ThrowIfNull(persister);
        try
        {
            using var replay = PayjoinMethods.ReplaySenderEventLog(persister);
            using var state = replay.State();
            switch (state)
            {
                case SendSession.WithReplyKey withReplyKey:
                    CancelThenClose(withReplyKey.Inner.Cancel(), persister);
                    break;
                case SendSession.PollingForProposal polling:
                    CancelThenClose(polling.Inner.Cancel(), persister);
                    break;
                case SendSession.SenderPendingFallback pendingFallback:
                    using (var closeTransition = pendingFallback.Inner.Close())
                    {
                        closeTransition.Save(persister);
                    }

                    break;
            }

            return true;
        }
        catch (Exception ex) when (ex is SenderPersistedException or SenderReplayException or UniffiException)
        {
            return false;
        }
    }

    private static void CancelThenClose(SenderCancelTransition cancelTransition, JsonSenderSessionPersister persister)
    {
        using (cancelTransition)
        using (var pendingFallback = cancelTransition.Save(persister))
        using (var closeTransition = pendingFallback.Close())
        {
            closeTransition.Save(persister);
        }
    }
}
