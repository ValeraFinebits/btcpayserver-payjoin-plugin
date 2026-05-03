using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.Payjoin.Data;
using Microsoft.EntityFrameworkCore;
using NBitcoin;
using Npgsql;
using Payjoin;
using System;
using System.Collections.Generic;
using System.Linq;
using SystemUri = System.Uri;

namespace BTCPayServer.Plugins.Payjoin.Services;

public sealed class PayjoinReceiverSessionStore
{
    private readonly PayjoinPluginDbContextFactory _pluginDbContextFactory;

    public PayjoinReceiverSessionStore(PayjoinPluginDbContextFactory pluginDbContextFactory)
    {
        ArgumentNullException.ThrowIfNull(pluginDbContextFactory);
        _pluginDbContextFactory = pluginDbContextFactory;
    }

    public PayjoinReceiverSessionState CreateSession(
        string invoiceId,
        string receiverAddress,
        string storeId,
        SystemUri ohttpRelayUrl,
        DateTimeOffset monitoringExpiresAt,
        out bool created)
    {
        ArgumentNullException.ThrowIfNull(ohttpRelayUrl);
        using var context = _pluginDbContextFactory.CreateContext();
        if (TryLoadSessionCore(context, invoiceId, out var existing))
        {
            created = false;
            return existing;
        }

        var now = DateTimeOffset.UtcNow;
        var sessionData = new PayjoinReceiverSessionData
        {
            InvoiceId = invoiceId,
            StoreId = storeId,
            ReceiverAddress = receiverAddress,
            OhttpRelayUrl = ohttpRelayUrl.AbsoluteUri,
            MonitoringExpiresAt = monitoringExpiresAt,
            CreatedAt = now,
            UpdatedAt = now
        };

        context.ReceiverSessions.Add(sessionData);
        try
        {
            context.SaveChanges();
            created = true;
            return CreateState(sessionData);
        }
        catch (DbUpdateException)
        {
            context.ChangeTracker.Clear();
            if (TryLoadSessionCore(context, invoiceId, out existing))
            {
                created = false;
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
        sessionData.CloseRequestedAt ??= requestedAt;
        if (changed)
        {
            sessionData.InitializedPollAfterCloseRequestConsumed = false;
        }
        if (!changed)
        {
            return false;
        }

        sessionData.UpdatedAt = requestedAt;
        context.SaveChanges();
        return true;
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

    public bool TryPersistContributedInput(string invoiceId, OutPoint outPoint)
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
        if (sessionData.ContributedInputTransactionId == transactionId && sessionData.ContributedInputOutputIndex == outputIndex)
        {
            return true;
        }

        sessionData.ContributedInputTransactionId = transactionId;
        sessionData.ContributedInputOutputIndex = outputIndex;
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
            var sequence = checked(lastSequence + 1);

            // TODO: Revisit receiver-session event retention before the log grows without bound.
            sessionData.UpdatedAt = createdAt;
            context.ReceiverSessionEvents.Add(new PayjoinReceiverSessionEventData
            {
                InvoiceId = invoiceId,
                Sequence = sequence,
                Event = @event,
                CreatedAt = createdAt
            });
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

    private static bool IsReceiverSessionEventSequenceConflict(DbUpdateException exception)
    {
        return exception.InnerException is PostgresException postgresException &&
               postgresException.SqlState == PostgresErrorCodes.UniqueViolation &&
               string.Equals(
                   postgresException.ConstraintName,
                   PayjoinPluginDbSchema.ReceiverSessionEventsInvoiceSequenceIndex,
                   StringComparison.Ordinal);
    }

    private static PayjoinReceiverSessionState CreateState(PayjoinReceiverSessionData sessionData, string[]? events = null)
    {
        return new PayjoinReceiverSessionState(
            sessionData.InvoiceId,
            sessionData.StoreId,
            sessionData.ReceiverAddress,
            new SystemUri(sessionData.OhttpRelayUrl, UriKind.Absolute),
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
        var sessionData = context.ReceiverSessions.AsNoTracking().SingleOrDefault(x => x.InvoiceId == invoiceId);
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
