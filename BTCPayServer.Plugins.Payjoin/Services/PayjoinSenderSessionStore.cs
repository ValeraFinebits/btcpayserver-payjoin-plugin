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
        IEnumerable<string> bootstrapEvents)
    {
        ArgumentNullException.ThrowIfNull(bootstrapEvents);
        var persistedEvents = bootstrapEvents.ToArray();
        if (persistedEvents.Length == 0)
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
            Status = PayjoinSenderSessionStatus.Pending,
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
    /// True when a pending session already pays the same original transaction, which is the
    /// double-payment guard for retried submissions of the same URI and PSBT.
    /// </summary>
    public bool HasPendingSessionForOriginal(string originalTransactionId)
    {
        using var context = _pluginDbContextFactory.CreateContext();
        return context.SenderSessions
            .AsNoTracking()
            .Any(x => x.OriginalTransactionId == originalTransactionId &&
                      x.Status == PayjoinSenderSessionStatus.Pending);
    }

    public bool CompleteSession(string senderSessionId, PayjoinSenderSessionStatus status, string? broadcastTransactionId, string? failureMessage)
    {
        if (status == PayjoinSenderSessionStatus.Pending)
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
