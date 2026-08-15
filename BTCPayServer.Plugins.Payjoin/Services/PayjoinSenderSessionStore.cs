using BTCPayServer.Plugins.Payjoin.Data;
using Microsoft.EntityFrameworkCore;
using Payjoin;
using System;
using System.Collections.Generic;
using System.Linq;

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
    string? RequestBaseUrl,
    PayjoinSenderSessionStatus Status,
    string? FailureMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string[] Events);

/// <summary>
/// Persists sender payjoin sessions and their rust-payjoin event logs, mirroring the receiver
/// session store: every state transition the library performs is appended as an event, and a
/// restart replays the log with ReplaySenderEventLog to resume from the same state.
/// </summary>
internal sealed class PayjoinSenderSessionStore
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
        string? pendingTransactionId = null,
        PayjoinSenderSessionStatus status = PayjoinSenderSessionStatus.Pending,
        string? requestBaseUrl = null)
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
            RequestBaseUrl = requestBaseUrl,
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

        context.SaveChanges();
        return CreateState(sessionData, persistedEvents);
    }

    public bool TryGetSession(string senderSessionId, out PayjoinSenderSessionState? session)
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

    public IReadOnlyCollection<PayjoinSenderSessionState> GetPendingSessions()
    {
        using var context = _pluginDbContextFactory.CreateContext();
        return LoadSessionsCore(context, pendingOnly: true);
    }

    public IReadOnlyCollection<PayjoinSenderSessionState> GetSessions(string storeId)
    {
        using var context = _pluginDbContextFactory.CreateContext();
        return LoadSessionsCore(context, pendingOnly: false, storeId);
    }

    /// <summary>
    /// True when a live session already pays the same original transaction, which is the
    /// double-payment guard for retried submissions of the same URI and PSBT. A session
    /// waiting on a signature counts: the operator has not signed it yet, but the coins are
    /// already committed to it.
    /// </summary>
    public bool HasPendingSessionForOriginal(string originalTransactionId)
    {
        using var context = _pluginDbContextFactory.CreateContext();
        return context.SenderSessions
            .AsNoTracking()
            .Any(x => x.OriginalTransactionId == originalTransactionId &&
                      (x.Status == PayjoinSenderSessionStatus.Pending ||
                       x.Status == PayjoinSenderSessionStatus.AwaitingSignature));
    }

    /// <summary>
    /// True when a live session already pays the same URI. This is the guard against paying an
    /// invoice twice: two attempts on one URI do not have to select the same coins, so their
    /// transaction ids can differ even though the payment is the same.
    /// </summary>
    public bool HasPendingSessionForBip21(string bip21)
    {
        using var context = _pluginDbContextFactory.CreateContext();
        return context.SenderSessions
            .AsNoTracking()
            .Any(x => x.Bip21 == bip21 &&
                      (x.Status == PayjoinSenderSessionStatus.Pending ||
                       x.Status == PayjoinSenderSessionStatus.AwaitingSignature));
    }

    /// <summary>
    /// Finds the session waiting on a given BTCPay pending transaction, so a collected
    /// signature can be matched back to the payjoin session that asked for it.
    /// </summary>
    public bool TryGetSessionByPendingTransactionId(string pendingTransactionId, out PayjoinSenderSessionState? session)
    {
        using var context = _pluginDbContextFactory.CreateContext();
        var sessionData = context.SenderSessions
            .AsNoTracking()
            .FirstOrDefault(x => x.PendingTransactionId == pendingTransactionId);
        if (sessionData is null)
        {
            session = null;
            return false;
        }

        session = CreateState(sessionData, LoadEventsCore(context, sessionData.SenderSessionId));
        return true;
    }

    /// <summary>
    /// Seeds the library state produced once the signed original arrived, and hands the session
    /// to the poller. The pending transaction id is cleared because that round is over.
    /// </summary>
    public bool StartSignedSession(string senderSessionId, IEnumerable<string> bootstrapEvents)
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

        var now = DateTimeOffset.UtcNow;
        var sequence = context.SenderSessionEvents
            .Where(x => x.SenderSessionId == senderSessionId)
            .Select(x => (int?)x.Sequence)
            .Max() ?? 0;
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

        sessionData.PendingTransactionId = null;
        sessionData.Status = PayjoinSenderSessionStatus.Pending;
        sessionData.UpdatedAt = now;
        context.SaveChanges();
        return true;
    }

    /// <summary>
    /// Parks a running session on a new pending transaction. The receiver's proposal is a
    /// different transaction from the original, so a wallet that cannot sign on the server has
    /// to sign a second time before the payjoin can be broadcast.
    /// </summary>
    public bool AwaitSignature(string senderSessionId, string pendingTransactionId)
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
        context.SaveChanges();
        return true;
    }

    public bool CompleteSession(string senderSessionId, PayjoinSenderSessionStatus status, string? broadcastTransactionId, string? failureMessage)
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

        sessionData.Status = status;
        sessionData.BroadcastTransactionId = broadcastTransactionId;
        sessionData.FailureMessage = failureMessage;
        sessionData.UpdatedAt = DateTimeOffset.UtcNow;
        context.SaveChanges();
        return true;
    }

    internal JsonSenderSessionPersister CreatePersister(string senderSessionId)
    {
        return new DatabaseBackedSenderPersister(this, senderSessionId);
    }

    private void AppendEvent(string senderSessionId, string @event)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var context = _pluginDbContextFactory.CreateContext();
            var sessionData = context.SenderSessions.SingleOrDefault(x => x.SenderSessionId == senderSessionId);
            if (sessionData is null)
            {
                throw new InvalidOperationException($"Payjoin sender session {senderSessionId} is no longer active.");
            }

            var createdAt = DateTimeOffset.UtcNow;
            var lastSequence = context.SenderSessionEvents
                .Where(x => x.SenderSessionId == senderSessionId)
                .Select(x => (int?)x.Sequence)
                .Max() ?? 0;

            sessionData.UpdatedAt = createdAt;
            context.SenderSessionEvents.Add(new PayjoinSenderSessionEventData
            {
                SenderSessionId = senderSessionId,
                Sequence = checked(lastSequence + 1),
                Event = @event,
                CreatedAt = createdAt
            });

            try
            {
                context.SaveChanges();
                return;
            }
            catch (DbUpdateException ex) when (IsSenderSessionEventSequenceConflict(ex))
            {
                if (attempt == maxAttempts)
                {
                    throw;
                }

                // A concurrent writer claimed the next sequence first; the unique
                // (SenderSessionId, Sequence) index is the durable ordering guard.
            }
        }
    }

    private string[] LoadEvents(string senderSessionId)
    {
        using var context = _pluginDbContextFactory.CreateContext();
        return LoadEventsCore(context, senderSessionId);
    }

    private bool IsSenderSessionEventSequenceConflict(DbUpdateException exception)
    {
        return _uniqueConstraintViolationDetector.IsUniqueConstraintViolation(exception, PayjoinPluginDbSchema.SenderSessionEventsSessionSequenceIndex);
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

    private static IReadOnlyCollection<PayjoinSenderSessionState> LoadSessionsCore(
        PayjoinPluginDbContext context,
        bool pendingOnly,
        string? storeId = null)
    {
        IQueryable<PayjoinSenderSessionData> query = context.SenderSessions.AsNoTracking();
        if (pendingOnly)
        {
            query = query.Where(x => x.Status == PayjoinSenderSessionStatus.Pending);
        }

        if (storeId is not null)
        {
            query = query.Where(x => x.StoreId == storeId);
        }

        var sessionData = query.OrderBy(x => x.CreatedAt).ToArray();
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
            sessionData.RequestBaseUrl,
            sessionData.Status,
            sessionData.FailureMessage,
            sessionData.CreatedAt,
            sessionData.UpdatedAt,
            events ?? []);
    }

    private sealed class DatabaseBackedSenderPersister : JsonSenderSessionPersister
    {
        private readonly PayjoinSenderSessionStore _store;
        private readonly string _senderSessionId;

        public DatabaseBackedSenderPersister(PayjoinSenderSessionStore store, string senderSessionId)
        {
            _store = store;
            _senderSessionId = senderSessionId;
        }

        public void Save(string @event)
        {
            _store.AppendEvent(_senderSessionId, @event);
        }

        public string[] Load() => _store.LoadEvents(_senderSessionId);

        public void Close()
        {
        }
    }
}
