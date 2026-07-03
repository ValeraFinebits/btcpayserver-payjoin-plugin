using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.Payjoin.Data;
using Microsoft.EntityFrameworkCore;
using Payjoin;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BTCPayServer.Plugins.Payjoin.Services;

public sealed class PayjoinReceiverSessionStore
{
    private readonly PayjoinPluginDbContextFactory _pluginDbContextFactory;
    private readonly IPayjoinUniqueConstraintViolationDetector _uniqueConstraintViolationDetector;

    internal PayjoinReceiverSessionStore(
        PayjoinPluginDbContextFactory pluginDbContextFactory,
        IPayjoinUniqueConstraintViolationDetector uniqueConstraintViolationDetector)
    {
        ArgumentNullException.ThrowIfNull(pluginDbContextFactory);
        ArgumentNullException.ThrowIfNull(uniqueConstraintViolationDetector);
        _pluginDbContextFactory = pluginDbContextFactory;
        _uniqueConstraintViolationDetector = uniqueConstraintViolationDetector;
    }

    internal PayjoinReceiverSessionState CreateSession(
        string invoiceId,
        string receiverAddress,
        string storeId,
        DateTimeOffset monitoringExpiresAt,
        IEnumerable<string> bootstrapEvents)
    {
        ArgumentNullException.ThrowIfNull(bootstrapEvents);

        var persistedEvents = bootstrapEvents.ToArray();
        if (persistedEvents.Length == 0)
        {
            throw new ArgumentException("Bootstrap events must contain the initial receiver session state.", nameof(bootstrapEvents));
        }

        using var context = _pluginDbContextFactory.CreateContext();
        var sessionData = LoadSessionDataCore(context, invoiceId, asNoTracking: false);
        var now = DateTimeOffset.UtcNow;

        if (sessionData is not null)
        {
            return CreateState(sessionData, LoadEventsCore(context, invoiceId));
        }
        sessionData = new PayjoinReceiverSessionData
        {
            InvoiceId = invoiceId,
            StoreId = storeId,
            ReceiverAddress = receiverAddress,
            MonitoringExpiresAt = monitoringExpiresAt,
            CreatedAt = now,
            UpdatedAt = now
        };
        context.ReceiverSessions.Add(sessionData);

        AddBootstrapEvents(context, invoiceId, persistedEvents, now);

        try
        {
            context.SaveChanges();
            return CreateState(sessionData, persistedEvents);
        }
        catch (DbUpdateException ex) when (IsReceiverSessionConflict(ex))
        {
            using var recoveryContext = _pluginDbContextFactory.CreateContext();
            if (TryLoadSessionCore(recoveryContext, invoiceId, out var existing))
            {
                return existing;
            }

            throw;
        }
    }

    public bool TryGetSession(string invoiceId, out PayjoinReceiverSessionState? session)
    {
        using var context = _pluginDbContextFactory.CreateContext();
        return TryLoadSessionCore(context, invoiceId, out session);
    }

    public IReadOnlyCollection<PayjoinReceiverSessionState> GetSessions()
    {
        using var context = _pluginDbContextFactory.CreateContext();
        return LoadSessionsCore(context);
    }

    public bool RemoveSession(string invoiceId)
    {
        using var context = _pluginDbContextFactory.CreateContext();
        var sessionData = context.ReceiverSessions.SingleOrDefault(x => x.InvoiceId == invoiceId);
        if (sessionData is null)
        {
            return false;
        }

        var sessionEvents = context.ReceiverSessionEvents
            .Where(x => x.InvoiceId == invoiceId)
            .ToArray();
        if (sessionEvents.Length > 0)
        {
            context.ReceiverSessionEvents.RemoveRange(sessionEvents);
        }

        var inputReservations = context.ReceiverInputReservations
            .Where(x => x.InvoiceId == invoiceId)
            .ToArray();
        if (inputReservations.Length > 0)
        {
            context.ReceiverInputReservations.RemoveRange(inputReservations);
        }

        context.ReceiverSessions.Remove(sessionData);
        context.SaveChanges();
        return true;
    }

    public bool RequestClose(string invoiceId, InvoiceStatus invoiceStatus)
    {
        using var context = _pluginDbContextFactory.CreateContext();
        var sessionData = context.ReceiverSessions.SingleOrDefault(x => x.InvoiceId == invoiceId);
        if (sessionData is null)
        {
            return false;
        }

        var requestedAt = DateTimeOffset.UtcNow;
        var changed = !sessionData.IsCloseRequested || sessionData.CloseInvoiceStatus != invoiceStatus;
        sessionData.IsCloseRequested = true;
        sessionData.CloseInvoiceStatus = invoiceStatus;
        if (changed)
        {
            sessionData.CloseRequestedAt = requestedAt;
            sessionData.InitializedPollAfterCloseRequestConsumed = false;
        }
        else
        {
            sessionData.CloseRequestedAt ??= requestedAt;
        }
        if (!changed)
        {
            return false;
        }

        sessionData.UpdatedAt = requestedAt;
        context.SaveChanges();
        return true;
    }

    public bool TryReserveContributedInput(
        string storeId,
        string invoiceId,
        NBitcoin.OutPoint outPoint,
        DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(outPoint);
        using var context = _pluginDbContextFactory.CreateContext();
        var sessionData = context.ReceiverSessions.SingleOrDefault(x => x.InvoiceId == invoiceId);
        if (sessionData is null)
        {
            return false;
        }

        var transactionId = outPoint.Hash.ToString();
        var outputIndex = checked((long)outPoint.N);
        if (!string.Equals(sessionData.StoreId, storeId, StringComparison.Ordinal))
        {
            return false;
        }

        var existingReservation = context.ReceiverInputReservations
            .SingleOrDefault(x => x.TransactionId == transactionId && x.OutputIndex == outputIndex);
        if (existingReservation is not null)
        {
            if (string.Equals(existingReservation.InvoiceId, invoiceId, StringComparison.Ordinal))
            {
                return sessionData.ContributedInputTransactionId == transactionId &&
                       sessionData.ContributedInputOutputIndex == outputIndex;
            }

            return false;
        }

        if (sessionData.ContributedInputTransactionId is not null || sessionData.ContributedInputOutputIndex is not null)
        {
            return sessionData.ContributedInputTransactionId == transactionId &&
                   sessionData.ContributedInputOutputIndex == outputIndex;
        }

        var reservedAt = DateTimeOffset.UtcNow;
        sessionData.ContributedInputTransactionId = transactionId;
        sessionData.ContributedInputOutputIndex = outputIndex;
        sessionData.UpdatedAt = reservedAt;
        context.ReceiverInputReservations.Add(new PayjoinReceiverInputReservationData
        {
            InvoiceId = invoiceId,
            StoreId = storeId,
            TransactionId = transactionId,
            OutputIndex = outputIndex,
            ReservedAt = reservedAt,
            ExpiresAt = expiresAt
        });

        try
        {
            context.SaveChanges();
            return true;
        }
        catch (DbUpdateException ex) when (IsReceiverInputReservationConflict(ex))
        {
            context.ChangeTracker.Clear();

            var winningReservation = context.ReceiverInputReservations
                .AsNoTracking()
                .SingleOrDefault(x => x.TransactionId == transactionId && x.OutputIndex == outputIndex);
            if (winningReservation is null ||
                !string.Equals(winningReservation.InvoiceId, invoiceId, StringComparison.Ordinal))
            {
                return false;
            }

            var winningSession = context.ReceiverSessions
                .AsNoTracking()
                .SingleOrDefault(x => x.InvoiceId == invoiceId);
            return winningSession?.ContributedInputTransactionId == transactionId &&
                   winningSession.ContributedInputOutputIndex == outputIndex;
        }
    }

    public int CleanupExpiredInputReservations(DateTimeOffset now)
    {
        using var context = _pluginDbContextFactory.CreateContext();
        var expiredReservations = context.ReceiverInputReservations
            .Where(x => x.ExpiresAt <= now)
            .ToArray();
        if (expiredReservations.Length == 0)
        {
            return 0;
        }

        var impactedInvoiceIds = expiredReservations
            .Select(x => x.InvoiceId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var impactedSessions = context.ReceiverSessions
            .Where(x => impactedInvoiceIds.Contains(x.InvoiceId))
            .ToDictionary(x => x.InvoiceId, StringComparer.Ordinal);

        foreach (var expiredReservation in expiredReservations)
        {
            if (!impactedSessions.TryGetValue(expiredReservation.InvoiceId, out var sessionData))
            {
                continue;
            }

            if (string.Equals(sessionData.ContributedInputTransactionId, expiredReservation.TransactionId, StringComparison.Ordinal) &&
                sessionData.ContributedInputOutputIndex == expiredReservation.OutputIndex)
            {
                sessionData.ContributedInputTransactionId = null;
                sessionData.ContributedInputOutputIndex = null;
                sessionData.UpdatedAt = now;
            }
        }

        context.ReceiverInputReservations.RemoveRange(expiredReservations);
        context.SaveChanges();
        return expiredReservations.Length;
    }

    public bool TryConsumeInitializedPollAfterCloseRequest(string invoiceId)
    {
        using var context = _pluginDbContextFactory.CreateContext();
        var sessionData = context.ReceiverSessions.SingleOrDefault(x => x.InvoiceId == invoiceId);
        if (sessionData is null ||
            !sessionData.IsCloseRequested ||
            sessionData.InitializedPollAfterCloseRequestConsumed)
        {
            return false;
        }

        sessionData.InitializedPollAfterCloseRequestConsumed = true;
        sessionData.UpdatedAt = DateTimeOffset.UtcNow;
        context.SaveChanges();
        return true;
    }

    internal JsonReceiverSessionPersister CreatePersister(PayjoinReceiverSessionState session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new DatabaseBackedReceiverPersister(this, session.InvoiceId);
    }

    private void AppendEvent(string invoiceId, string @event)
    {
        AppendEventsWithAccountingUpdate(invoiceId, [@event], updateBridge: null);
    }

    /// <summary>
    /// Appends session events and applies an accounting-bridge update in one database transaction, so a
    /// restart cannot observe the session state advanced while the accounting record lags behind.
    /// </summary>
    internal void AppendEventsWithAccountingUpdate(string invoiceId, IReadOnlyList<string> events, Action<PayjoinAccountingBridgeData>? updateBridge)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0 && updateBridge is null)
        {
            return;
        }

        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var context = _pluginDbContextFactory.CreateContext();
            var sessionData = context.ReceiverSessions.SingleOrDefault(x => x.InvoiceId == invoiceId);
            if (sessionData is null)
            {
                throw new InvalidOperationException($"Payjoin receiver session {invoiceId} is no longer active.");
            }

            var createdAt = DateTimeOffset.UtcNow;
            var lastSequence = context.ReceiverSessionEvents
                .Where(x => x.InvoiceId == invoiceId)
                .Select(x => (int?)x.Sequence)
                .Max() ?? 0;

            // TODO: Revisit receiver-session event retention before the log grows without bound.
            sessionData.UpdatedAt = createdAt;
            foreach (var @event in events)
            {
                lastSequence = checked(lastSequence + 1);
                context.ReceiverSessionEvents.Add(new PayjoinReceiverSessionEventData
                {
                    InvoiceId = invoiceId,
                    Sequence = lastSequence,
                    Event = @event,
                    CreatedAt = createdAt
                });
            }

            if (updateBridge is not null)
            {
                var bridge = context.AccountingBridges.SingleOrDefault(x => x.InvoiceId == invoiceId);
                if (bridge is not null)
                {
                    updateBridge(bridge);
                    bridge.UpdatedAt = createdAt;
                }
            }

            try
            {
                context.SaveChanges();
                return;
            }
            catch (DbUpdateException ex) when (IsReceiverSessionEventSequenceConflict(ex))
            {
                if (attempt == maxAttempts)
                {
                    throw;
                }

                // A concurrent app instance may have claimed the next sequence first.
                // The unique (InvoiceId, Sequence) index is the durable ordering guard.
            }
        }
    }

    private bool IsReceiverSessionEventSequenceConflict(DbUpdateException exception)
    {
        return _uniqueConstraintViolationDetector.IsUniqueConstraintViolation(exception, PayjoinPluginDbSchema.ReceiverSessionEventsInvoiceSequenceIndex);
    }

    private bool IsReceiverSessionConflict(DbUpdateException exception)
    {
        return _uniqueConstraintViolationDetector.IsUniqueConstraintViolation(exception, PayjoinPluginDbSchema.ReceiverSessionsPrimaryKey);
    }

    private bool IsReceiverInputReservationConflict(DbUpdateException exception)
    {
        return _uniqueConstraintViolationDetector.IsUniqueConstraintViolation(exception, PayjoinPluginDbSchema.ReceiverInputReservationsOutPointIndex);
    }

    private static PayjoinReceiverSessionState CreateState(PayjoinReceiverSessionData sessionData, string[]? events = null)
    {
        return new PayjoinReceiverSessionState(
            sessionData.InvoiceId,
            sessionData.StoreId,
            sessionData.ReceiverAddress,
            sessionData.MonitoringExpiresAt,
            sessionData.CreatedAt,
            sessionData.UpdatedAt,
            sessionData.IsCloseRequested,
            sessionData.CloseInvoiceStatus,
            sessionData.CloseRequestedAt,
            sessionData.InitializedPollAfterCloseRequestConsumed,
            sessionData.ContributedInputTransactionId,
            sessionData.ContributedInputOutputIndex,
            events ?? []);
    }

    private static IReadOnlyCollection<PayjoinReceiverSessionState> LoadSessionsCore(PayjoinPluginDbContext context)
    {
        var sessionData = context.ReceiverSessions
            .AsNoTracking()
            .OrderBy(x => x.CreatedAt)
            .ToArray();
        var sessionEvents = context.ReceiverSessionEvents
            .AsNoTracking()
            .OrderBy(x => x.InvoiceId)
            .ThenBy(x => x.Sequence)
            .ToArray()
            .GroupBy(x => x.InvoiceId)
            .ToDictionary(x => x.Key, x => x.Select(e => e.Event).ToArray());

        return sessionData
            .Select(row => CreateState(row, sessionEvents.GetValueOrDefault(row.InvoiceId)))
            .ToArray();
    }

    private static bool TryLoadSessionCore(
        PayjoinPluginDbContext context,
        string invoiceId,
        out PayjoinReceiverSessionState session)
    {
        var sessionData = LoadSessionDataCore(context, invoiceId, asNoTracking: true);
        if (sessionData is null)
        {
            session = null!;
            return false;
        }

        session = CreateState(sessionData, LoadEventsCore(context, invoiceId));
        return true;
    }

    private static string[] LoadEventsCore(PayjoinPluginDbContext context, string invoiceId)
    {
        return context.ReceiverSessionEvents
            .AsNoTracking()
            .Where(x => x.InvoiceId == invoiceId)
            .OrderBy(x => x.Sequence)
            .Select(x => x.Event)
            .ToArray();
    }

    private static PayjoinReceiverSessionData? LoadSessionDataCore(
        PayjoinPluginDbContext context,
        string invoiceId,
        bool asNoTracking)
    {
        IQueryable<PayjoinReceiverSessionData> query = context.ReceiverSessions;
        if (asNoTracking)
        {
            query = query.AsNoTracking();
        }

        return query.SingleOrDefault(x => x.InvoiceId == invoiceId);
    }

    private static void AddBootstrapEvents(PayjoinPluginDbContext context, string invoiceId, IEnumerable<string> bootstrapEvents, DateTimeOffset createdAt)
    {
        var sequence = 0;
        foreach (var @event in bootstrapEvents)
        {
            sequence++;
            context.ReceiverSessionEvents.Add(new PayjoinReceiverSessionEventData
            {
                InvoiceId = invoiceId,
                Sequence = sequence,
                Event = @event,
                CreatedAt = createdAt
            });
        }
    }

    private string[] LoadEvents(string invoiceId)
    {
        using var context = _pluginDbContextFactory.CreateContext();
        return LoadEventsCore(context, invoiceId);
    }

    private sealed class DatabaseBackedReceiverPersister : JsonReceiverSessionPersister
    {
        private readonly PayjoinReceiverSessionStore _store;
        private readonly string _invoiceId;

        public DatabaseBackedReceiverPersister(PayjoinReceiverSessionStore store, string invoiceId)
        {
            _store = store;
            _invoiceId = invoiceId;
        }

        public void Save(string @event)
        {
            _store.AppendEvent(_invoiceId, @event);
        }

        public string[] Load() => _store.LoadEvents(_invoiceId);

        public void Close()
        {
        }
    }
}
