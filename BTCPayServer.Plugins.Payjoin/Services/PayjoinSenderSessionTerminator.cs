using BTCPayServer.HostedServices;
using BTCPayServer.Plugins.Payjoin.Data;
using NBitcoin;
using NBXplorer;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

/// <summary>What ending a session came to.</summary>
internal enum PayjoinSenderTerminalOutcome
{
    /// <summary>The signed original went out and the session completed as a fallback.</summary>
    FallbackBroadcast,

    /// <summary>The session ended with nothing broadcast; the failure message says why.</summary>
    Failed,

    /// <summary>
    /// The fallback was refused for a reason that may pass. Nothing changed; the caller leaves
    /// the session where it is and the next tick tries again.
    /// </summary>
    RetryLater
}

/// <summary>
/// The one way a session ends when its payjoin cannot happen. The payjoin is over, but the
/// payment need not be: once the original is signed the operator has authorised it, and the
/// receiver already holds a copy it could broadcast at any moment, so recording a failure while
/// keeping the transaction off the network would only misstate what can still happen. Whenever
/// the signed original exists it goes out and the session completes as a fallback; only a
/// session with nothing signed, or whose fallback the network permanently refuses, ends with
/// nothing broadcast. Either way the session's external rows are released.
/// </summary>
internal static class PayjoinSenderSessionTerminator
{
    internal static async Task<PayjoinSenderTerminalOutcome> EndAsync(
        PayjoinSenderSessionStore senderSessionStore,
        PendingTransactionService pendingTransactionService,
        ExplorerClient explorerClient,
        Network network,
        PayjoinSenderSessionState session,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(senderSessionStore);
        ArgumentNullException.ThrowIfNull(session);

        if (session.OriginalTransactionHex is null)
        {
            return await CompleteAsync(
                senderSessionStore, pendingTransactionService, session,
                PayjoinSenderSessionStatus.Failed, broadcastTransactionId: null, reason).ConfigureAwait(false);
        }

        Transaction fallbackTransaction;
        try
        {
            fallbackTransaction = Transaction.Parse(session.OriginalTransactionHex, network);
        }
        catch (FormatException)
        {
            return await CompleteAsync(
                senderSessionStore, pendingTransactionService, session,
                PayjoinSenderSessionStatus.Failed, broadcastTransactionId: null,
                $"{reason}; the stored fallback could not be parsed").ConfigureAwait(false);
        }

        try
        {
            await PayjoinSenderBroadcaster
                .BroadcastAsync(explorerClient, fallbackTransaction, cancellationToken).ConfigureAwait(false);
        }
        catch (PayjoinSenderBroadcastException ex) when (!ex.Permanent)
        {
            return PayjoinSenderTerminalOutcome.RetryLater;
        }
        catch (PayjoinSenderBroadcastException ex)
        {
            // The coins went somewhere the plugin did not send them; retrying cannot help, and
            // where they went is on the chain for the operator to see.
            return await CompleteAsync(
                senderSessionStore, pendingTransactionService, session,
                PayjoinSenderSessionStatus.Failed, broadcastTransactionId: null,
                $"{reason}; the plain payment was refused: {ex.Message}").ConfigureAwait(false);
        }

        PayjoinSenderSessionCloser.TryClose(senderSessionStore.CreatePersister(session.SenderSessionId));
        return await CompleteAsync(
            senderSessionStore, pendingTransactionService, session,
            PayjoinSenderSessionStatus.CompletedFallback,
            fallbackTransaction.GetHash().ToString(), reason).ConfigureAwait(false);
    }

    private static async Task<PayjoinSenderTerminalOutcome> CompleteAsync(
        PayjoinSenderSessionStore senderSessionStore,
        PendingTransactionService pendingTransactionService,
        PayjoinSenderSessionState session,
        PayjoinSenderSessionStatus status,
        string? broadcastTransactionId,
        string? failureMessage)
    {
        senderSessionStore.CompleteSession(session.SenderSessionId, status, broadcastTransactionId, failureMessage);
        await PayjoinSenderSessionResourceReleaser
            .ReleaseAsync(pendingTransactionService, senderSessionStore, session).ConfigureAwait(false);
        return status == PayjoinSenderSessionStatus.CompletedFallback
            ? PayjoinSenderTerminalOutcome.FallbackBroadcast
            : PayjoinSenderTerminalOutcome.Failed;
    }
}
