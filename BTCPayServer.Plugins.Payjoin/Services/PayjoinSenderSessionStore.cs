using BTCPayServer.Plugins.Payjoin.Data;
using Microsoft.EntityFrameworkCore;
using Payjoin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal sealed record PayjoinSenderSessionState(
    string SenderSessionId,
    string StoreId,
    string Bip21,
    string DestinationAddress,
    long AmountSats,
    string OriginalTransactionId,
    string? BroadcastTransactionId,
    string? PendingTransactionId,
    string? CoinReservationTransactionId,
    string? RequestBaseUrl,
    long FeeRateSatPerKwu,
    string[] OutpointsUsed,
    string? OriginalTransactionHex,
    PayjoinSenderSessionStatus Status,
    string? FailureMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string[] Events,
    bool PaymentExposed = false);

/// <summary>
/// Persists sender payjoin sessions and their rust-payjoin event logs, mirroring the receiver
/// session store: every state transition the library performs is appended as an event, and a
/// restart replays the log with ReplaySenderEventLog to resume from the same state.
/// </summary>
public sealed class PayjoinSenderSessionStore
{
    private readonly PayjoinPluginDbContextFactory _pluginDbContextFactory;
    private readonly IPayjoinUniqueConstraintViolationDetector _uniqueConstraintViolationDetector;

    internal PayjoinSenderSessionStore(
        PayjoinPluginDbContextFactory pluginDbContextFactory,
        IPayjoinUniqueConstraintViolationDetector uniqueConstraintViolationDetector)
    {
        ArgumentNullException.ThrowIfNull(pluginDbContextFactory);
        ArgumentNullException.ThrowIfNull(uniqueConstraintViolationDetector);
        _pluginDbContextFactory = pluginDbContextFactory;
        _uniqueConstraintViolationDetector = uniqueConstraintViolationDetector;
    }

    internal PayjoinSenderSessionState CreateSession(
        string senderSessionId,
        string storeId,
        string bip21,
        string destinationAddress,
        long amountSats,
        string originalTransactionId,
        IEnumerable<string> bootstrapEvents,
        long feeRateSatPerKwu = 0,
        IEnumerable<string>? outpointsUsed = null,
        string? originalTransactionHex = null,
        string? pendingTransactionId = null,
        PayjoinSenderSessionStatus status = PayjoinSenderSessionStatus.Pending,
        string? requestBaseUrl = null,
        string? coinReservationTransactionId = null)
    {
        ArgumentNullException.ThrowIfNull(bootstrapEvents);
        var persistedEvents = bootstrapEvents.ToArray();
        // A session waiting on an off-server signature has no library state yet, because the
        // sender state machine needs the signed original before it can be built. Every other
        // status must carry the state the library produced.
        if (persistedEvents.Length == 0 && status != PayjoinSenderSessionStatus.AwaitingSignature)
        {
            throw new ArgumentException("Bootstrap events must contain the initial sender session state.", nameof(bootstrapEvents));
        }

        using var context = _pluginDbContextFactory.CreateContext();
        var now = DateTimeOffset.UtcNow;
        var sessionData = new PayjoinSenderSessionData
        {
            SenderSessionId = senderSessionId,
            StoreId = storeId,
            Bip21 = bip21,
            DestinationAddress = destinationAddress,
            AmountSats = amountSats,
            OriginalTransactionId = originalTransactionId,
            PendingTransactionId = pendingTransactionId,
            CoinReservationTransactionId = coinReservationTransactionId,
            RequestBaseUrl = requestBaseUrl,
            FeeRateSatPerKwu = feeRateSatPerKwu,
            OutpointsUsed = outpointsUsed?.ToArray() ?? [],
            OriginalTransactionHex = originalTransactionHex,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now
        };
        context.SenderSessions.Add(sessionData);

        var sequence = 0;
        foreach (var @event in persistedEvents)
        {
            sequence++;
            context.SenderSessionEvents.Add(new PayjoinSenderSessionEventData
            {
                SenderSessionId = senderSessionId,
                Sequence = sequence,
                Event = @event,
                CreatedAt = now
            });
        }

        // One row per coin, keyed by the outpoint itself. A live session holds its coins at the
        // database level: a second session for any of them, from any store, fails this insert.
        // The read first gives the common case a clear answer on every provider; the primary
        // key is what holds when two writers race past it on Postgres.
        var sessionOutpoints = sessionData.OutpointsUsed;
        if (sessionOutpoints.Length > 0 &&
            context.SenderSessionOutpoints.Any(x => sessionOutpoints.Contains(x.Outpoint)))
        {
            throw new PayjoinSenderDuplicateSessionException("A payjoin session already holds one of the selected coins.");
        }

        foreach (var outpoint in sessionData.OutpointsUsed)
        {
            context.SenderSessionOutpoints.Add(new PayjoinSenderSessionOutpointData
            {
                Outpoint = outpoint,
                SenderSessionId = senderSessionId
            });
        }

        try
        {
            context.SaveChanges();
        }
        catch (DbUpdateException ex) when (_uniqueConstraintViolationDetector.IsUniqueConstraintViolation(
            ex, PayjoinPluginDbSchema.SenderSessionsLiveBip21Index))
        {
            // A concurrent writer created a live session for the same URI between the read-side
            // check and this save. The unique live index is the guard that holds across
            // processes and restarts.
            throw new PayjoinSenderDuplicateSessionException("A payjoin session already pays this URI.", ex);
        }
        catch (DbUpdateException ex) when (_uniqueConstraintViolationDetector.IsUniqueConstraintViolation(
            ex, PayjoinPluginDbSchema.SenderSessionOutpointsPrimaryKey))
        {
            throw new PayjoinSenderDuplicateSessionException("A payjoin session already holds one of the selected coins.", ex);
        }

        return CreateState(sessionData, persistedEvents);
    }

    internal bool TryGetSession(string senderSessionId, out PayjoinSenderSessionState? session)
    {
        using var context = _pluginDbContextFactory.CreateContext();
        var sessionData = context.SenderSessions
            .AsNoTracking()
            .SingleOrDefault(x => x.SenderSessionId == senderSessionId);
        if (sessionData is null)
        {
            session = null;
            return false;
        }

        session = CreateState(sessionData, LoadEventsCore(context, senderSessionId));
        return true;
    }

    internal IReadOnlyCollection<PayjoinSenderSessionState> GetPendingSessions()
        => LoadSessions(x => x.Status == PayjoinSenderSessionStatus.Pending);

    internal IReadOnlyCollection<PayjoinSenderSessionState> GetSessions(string storeId)
        => LoadSessions(x => x.StoreId == storeId);

    /// <summary>
    /// True when a live session already pays the same original transaction, which is the
    /// double-payment guard for retried submissions of the same URI and PSBT. A session
    /// waiting on a signature counts: the operator has not signed it yet, but the coins are
    /// already committed to it.
    /// </summary>
    internal bool HasPendingSessionForOriginal(string originalTransactionId)
    {
        using var context = _pluginDbContextFactory.CreateContext();
        return context.SenderSessions
            .AsNoTracking()
            .Any(x => x.OriginalTransactionId == originalTransactionId &&
                      (x.Status == PayjoinSenderSessionStatus.Pending ||
                       x.Status == PayjoinSenderSessionStatus.AwaitingSignature));
    }

    /// <summary>
    /// Every session that waits for a signature. The poller sweeps these each tick, because the
    /// signature arrives as an in-memory event that a restart can lose, and because a cancelled
    /// or expired pending transaction produces no event this plugin can act on at all.
    /// </summary>
    internal IReadOnlyCollection<PayjoinSenderSessionState> GetSessionsAwaitingSignature()
        => LoadSessions(x => x.Status == PayjoinSenderSessionStatus.AwaitingSignature);

    /// <summary>
    /// The outpoints every live session of a store holds. A session commits its coins the moment
    /// it builds the original, and it keeps them until it ends, so the next transaction the store
    /// builds must leave them alone.
    /// </summary>
    internal IReadOnlyCollection<string> GetOutpointsHeldByLiveSessions(string storeId)
    {
        // The outpoint rows exist exactly while their session is live, so the reservation table
        // is the answer; no status filter or array flattening is needed.
        using var context = _pluginDbContextFactory.CreateContext();
        return context.SenderSessionOutpoints
            .AsNoTracking()
            .Where(x => x.Session.StoreId == storeId)
            .Select(x => x.Outpoint)
            .ToArray();
    }

    /// <summary>
    /// True when a live session of this store already pays the same URI. This is the guard
    /// against paying an invoice twice: two attempts on one URI do not have to select the same
    /// coins, so their transaction ids can differ even though the payment is the same. Scoped
    /// by store, matching the unique live index: another store paying the same URI is its own
    /// payment.
    /// </summary>
    internal bool HasPendingSessionForBip21(string storeId, string bip21)
    {
        using var context = _pluginDbContextFactory.CreateContext();
        return context.SenderSessions
            .AsNoTracking()
            .Any(x => x.StoreId == storeId &&
                      x.Bip21 == bip21 &&
                      (x.Status == PayjoinSenderSessionStatus.Pending ||
                       x.Status == PayjoinSenderSessionStatus.AwaitingSignature));
    }

    /// <summary>
    /// Every live session whose coins a pending transaction holds. The sweep watches these
    /// rows for what the operator does on BTCPay's own screen: a manual broadcast of the plain
    /// payment, or a cancellation of it. A session parked on its second signature counts: its
    /// reservation is just as actionable there as while the poller drives it.
    /// </summary>
    internal IReadOnlyCollection<PayjoinSenderSessionState> GetLiveSessionsWithCoinReservations()
        => LoadSessions(x => (x.Status == PayjoinSenderSessionStatus.Pending ||
                              x.Status == PayjoinSenderSessionStatus.AwaitingSignature) &&
                             x.CoinReservationTransactionId != null, includeEvents: false);

    /// <summary>
    /// Sessions that ended while still pointing at a signing request or a coin reservation.
    /// The release runs after the completion write, so a crash between the two leaves rows
    /// holding coins a dead session no longer needs; the sweep finishes the release.
    /// </summary>
    internal IReadOnlyCollection<PayjoinSenderSessionState> GetSessionsWithDanglingResources()
        => LoadSessions(x => (x.CoinReservationTransactionId != null || x.PendingTransactionId != null) &&
                             x.Status != PayjoinSenderSessionStatus.Pending &&
                             x.Status != PayjoinSenderSessionStatus.AwaitingSignature, includeEvents: false);

    /// <summary>
    /// Records that a session's external rows have been released, so the release is not
    /// repeated on every sweep.
    /// </summary>
    internal void ClearReleasedResources(PayjoinSenderSessionState released)
    {
        // Only clear the exact handles that were cancelled. A newer handle must remain visible
        // to the recovery sweep, even when this release began with a stale session snapshot.
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var context = _pluginDbContextFactory.CreateContext();
            var sessionData = context.SenderSessions.SingleOrDefault(x => x.SenderSessionId == released.SenderSessionId);
            if (sessionData is null ||
                (sessionData.CoinReservationTransactionId is null && sessionData.PendingTransactionId is null))
            {
                return;
            }

            // Only a finished session releases its rows; on a live one the same columns are
            // the working handles of the signing round and the reservation.
            if (sessionData.Status is PayjoinSenderSessionStatus.Pending or PayjoinSenderSessionStatus.AwaitingSignature)
            {
                return;
            }

            if (sessionData.CoinReservationTransactionId == released.CoinReservationTransactionId)
                sessionData.CoinReservationTransactionId = null;
            if (sessionData.PendingTransactionId == released.PendingTransactionId)
                sessionData.PendingTransactionId = null;
            sessionData.UpdatedAt = DateTimeOffset.UtcNow;
            try
            {
                context.SaveChanges();
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < maxAttempts)
            {
            }
        }
    }

    /// <summary>
    /// Seeds the library state produced once the signed original arrived, and hands the session
    /// to the poller. The signing round is over, so the pending transaction stops being a
    /// signature to wait for; it becomes the session's coin reservation instead. Core keeps
    /// excluding a Signed row's outpoints, which is exactly what a live session needs, and the
    /// row's broadcast button stays available as the operator's manual fallback.
    /// </summary>
    internal bool StartSignedSession(string senderSessionId, IEnumerable<string> bootstrapEvents, string originalTransactionHex)
    {
        ArgumentNullException.ThrowIfNull(bootstrapEvents);
        var persistedEvents = bootstrapEvents.ToArray();
        if (persistedEvents.Length == 0)
        {
            throw new ArgumentException("A started session must carry the initial sender state.", nameof(bootstrapEvents));
        }

        using var context = _pluginDbContextFactory.CreateContext();
        var sessionData = context.SenderSessions.SingleOrDefault(x => x.SenderSessionId == senderSessionId);
        if (sessionData is null || sessionData.Status != PayjoinSenderSessionStatus.AwaitingSignature)
        {
            return false;
        }

        // Bootstrap events are by definition the session's first: a session that still waits
        // for its signature carries no library state. Numbering from one, instead of after the
        // stored maximum, makes the unique (session, sequence) index the seeding guard itself.
        // Two workers can pass the status check together, because the status read and a
        // maximum read are separate queries and a commit can land between them; with fixed
        // numbering the loser's insert of sequence one conflicts and its whole save, the
        // status flip included, rolls back.
        var now = DateTimeOffset.UtcNow;
        var sequence = 0;
        foreach (var @event in persistedEvents)
        {
            sequence++;
            context.SenderSessionEvents.Add(new PayjoinSenderSessionEventData
            {
                SenderSessionId = senderSessionId,
                Sequence = sequence,
                Event = @event,
                CreatedAt = now
            });
        }

        sessionData.CoinReservationTransactionId = sessionData.PendingTransactionId;
        sessionData.PendingTransactionId = null;
        sessionData.OriginalTransactionHex = originalTransactionHex;
        sessionData.Status = PayjoinSenderSessionStatus.Pending;
        sessionData.UpdatedAt = now;
        try
        {
            context.SaveChanges();
        }
        catch (DbUpdateConcurrencyException)
        {
            // A concurrent transition changed the status after the read above.
            return false;
        }
        catch (DbUpdateException ex) when (_uniqueConstraintViolationDetector.IsUniqueConstraintViolation(
            ex, PayjoinPluginDbSchema.SenderSessionEventsSessionSequenceIndex))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Parks a running session on a new pending transaction. The receiver's proposal is a
    /// different transaction from the original, so a wallet that cannot sign on the server has
    /// to sign a second time before the payjoin can be broadcast.
    /// </summary>
    internal bool AwaitSignature(string senderSessionId, string pendingTransactionId)
    {
        using var context = _pluginDbContextFactory.CreateContext();
        var sessionData = context.SenderSessions.SingleOrDefault(x => x.SenderSessionId == senderSessionId);
        if (sessionData is null || sessionData.Status != PayjoinSenderSessionStatus.Pending)
        {
            return false;
        }

        sessionData.PendingTransactionId = pendingTransactionId;
        sessionData.Status = PayjoinSenderSessionStatus.AwaitingSignature;
        sessionData.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            context.SaveChanges();
        }
        catch (DbUpdateConcurrencyException)
        {
            // A concurrent transition changed the status after the read above. Whatever it was
            // wins; parking a session that is no longer running would resurrect it.
            return false;
        }

        return true;
    }

    internal bool CompleteSession(string senderSessionId, PayjoinSenderSessionStatus status, string? broadcastTransactionId, string? failureMessage, bool requireUnshared = false)
    {
        if (status is PayjoinSenderSessionStatus.Pending or PayjoinSenderSessionStatus.AwaitingSignature)
        {
            throw new ArgumentException("Completion requires a terminal status.", nameof(status));
        }

        using var context = _pluginDbContextFactory.CreateContext();
        var sessionData = context.SenderSessions.SingleOrDefault(x => x.SenderSessionId == senderSessionId);
        if (sessionData is null)
        {
            return false;
        }

        // The first terminal state wins. Two routes can reach one session at once: the operator
        // stopping it, and a signature collected a moment earlier arriving late. Whichever landed
        // first is what happened, and a later one must not rewrite the record.
        if (sessionData.Status is not (PayjoinSenderSessionStatus.Pending or PayjoinSenderSessionStatus.AwaitingSignature) ||
            (requireUnshared && sessionData.PaymentExposed))
        {
            return false;
        }

        sessionData.Status = status;
        sessionData.BroadcastTransactionId = broadcastTransactionId;
        sessionData.FailureMessage = failureMessage;
        sessionData.UpdatedAt = DateTimeOffset.UtcNow;
        // Free the coins in the same transaction that ends the session: a live session and its
        // outpoint rows exist together or not at all, and the concurrency token rolls both back
        // when another transition wins.
        context.SenderSessionOutpoints.RemoveRange(
            context.SenderSessionOutpoints.Where(x => x.SenderSessionId == senderSessionId).ToArray());
        try
        {
            context.SaveChanges();
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another transition committed first. The first terminal state is what happened;
            // this one must not rewrite it.
            return false;
        }

        return true;
    }

    internal JsonSenderSessionPersister CreatePersister(string senderSessionId)
    {
        return new DatabaseBackedSenderPersister(this, senderSessionId);
    }

    internal Task<IAsyncDisposable?> TryLockSessionAsync(string senderSessionId, CancellationToken cancellationToken)
        => PayjoinSenderSessionLock.TryAcquireAsync(_pluginDbContextFactory, senderSessionId, cancellationToken);

    internal bool TryMarkPaymentExposed(string senderSessionId)
    {
        using var context = _pluginDbContextFactory.CreateContext();
        var session = context.SenderSessions.SingleOrDefault(x => x.SenderSessionId == senderSessionId);
        if (session is null || session.Status is not (PayjoinSenderSessionStatus.Pending or PayjoinSenderSessionStatus.AwaitingSignature))
            return false;
        if (session.PaymentExposed)
            return true;
        session.PaymentExposed = true;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        try { context.SaveChanges(); }
        catch (DbUpdateConcurrencyException) { return false; }
        return true;
    }

    private void AppendEvent(string senderSessionId, string @event, int expectedSequence)
    {
        using var context = _pluginDbContextFactory.CreateContext();
        var sessionData = context.SenderSessions.SingleOrDefault(x => x.SenderSessionId == senderSessionId);
        if (sessionData is null ||
            sessionData.Status is not (PayjoinSenderSessionStatus.Pending or PayjoinSenderSessionStatus.AwaitingSignature))
        {
            throw new DbUpdateConcurrencyException($"Payjoin sender session {senderSessionId} is no longer active.");
        }

        // A transition belongs to the state that was replayed. Never append it after a winner:
        // the next tick must replay the winner's state and compute a new transition instead.
        var lastSequence = context.SenderSessionEvents
            .Where(x => x.SenderSessionId == senderSessionId)
            .Select(x => (int?)x.Sequence).Max() ?? 0;
        if (lastSequence != expectedSequence - 1)
            throw new DbUpdateConcurrencyException("The sender event log changed since it was loaded.");

        sessionData.UpdatedAt = DateTimeOffset.UtcNow;
        context.SenderSessionEvents.Add(new PayjoinSenderSessionEventData
        {
            SenderSessionId = senderSessionId,
            Sequence = expectedSequence,
            Event = @event,
            CreatedAt = sessionData.UpdatedAt
        });
        // The sequence unique index and session concurrency tokens guard concurrent saves.
        context.SaveChanges();
    }

    private string[] LoadEvents(string senderSessionId)
    {
        using var context = _pluginDbContextFactory.CreateContext();
        return LoadEventsCore(context, senderSessionId);
    }

    private static string[] LoadEventsCore(PayjoinPluginDbContext context, string senderSessionId)
    {
        return context.SenderSessionEvents
            .AsNoTracking()
            .Where(x => x.SenderSessionId == senderSessionId)
            .OrderBy(x => x.Sequence)
            .Select(x => x.Event)
            .ToArray();
    }

    private IReadOnlyCollection<PayjoinSenderSessionState> LoadSessions(
        Expression<Func<PayjoinSenderSessionData, bool>> predicate,
        bool includeEvents = true)
    {
        using var context = _pluginDbContextFactory.CreateContext();
        var sessionData = context.SenderSessions.AsNoTracking().Where(predicate).OrderBy(x => x.CreatedAt).ToArray();
        if (!includeEvents)
            return sessionData.Select(row => CreateState(row)).ToArray();

        var sessionIds = sessionData.Select(x => x.SenderSessionId).ToArray();
        var sessionEvents = context.SenderSessionEvents
            .AsNoTracking()
            .Where(x => sessionIds.Contains(x.SenderSessionId))
            .OrderBy(x => x.SenderSessionId)
            .ThenBy(x => x.Sequence)
            .ToArray()
            .GroupBy(x => x.SenderSessionId)
            .ToDictionary(x => x.Key, x => x.Select(e => e.Event).ToArray());

        return sessionData
            .Select(row => CreateState(row, sessionEvents.GetValueOrDefault(row.SenderSessionId)))
            .ToArray();
    }

    private static PayjoinSenderSessionState CreateState(PayjoinSenderSessionData sessionData, string[]? events = null)
    {
        return new PayjoinSenderSessionState(
            sessionData.SenderSessionId,
            sessionData.StoreId,
            sessionData.Bip21,
            sessionData.DestinationAddress,
            sessionData.AmountSats,
            sessionData.OriginalTransactionId,
            sessionData.BroadcastTransactionId,
            sessionData.PendingTransactionId,
            sessionData.CoinReservationTransactionId,
            sessionData.RequestBaseUrl,
            sessionData.FeeRateSatPerKwu,
            sessionData.OutpointsUsed ?? [],
            sessionData.OriginalTransactionHex,
            sessionData.Status,
            sessionData.FailureMessage,
            sessionData.CreatedAt,
            sessionData.UpdatedAt,
            events ?? [],
            sessionData.PaymentExposed);
    }

    private sealed class DatabaseBackedSenderPersister : JsonSenderSessionPersister
    {
        private readonly PayjoinSenderSessionStore _store;
        private readonly string _senderSessionId;
        private int _nextSequence;

        public DatabaseBackedSenderPersister(PayjoinSenderSessionStore store, string senderSessionId)
        {
            _store = store;
            _senderSessionId = senderSessionId;
            Load();
        }

        public void Save(string @event)
        {
            _store.AppendEvent(_senderSessionId, @event, _nextSequence);
            _nextSequence = checked(_nextSequence + 1);
        }

        public string[] Load()
        {
            var events = _store.LoadEvents(_senderSessionId);
            _nextSequence = checked(events.Length + 1);
            return events;
        }

        public void Close()
        {
        }
    }
}
