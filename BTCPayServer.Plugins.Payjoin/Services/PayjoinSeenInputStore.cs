using BTCPayServer.Plugins.Payjoin.Data;
using Microsoft.EntityFrameworkCore;
using Payjoin;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BTCPayServer.Plugins.Payjoin.Services;

/// <summary>
/// Persistent record of input outpoints the receiver has already seen across payjoin sessions.
/// Backs <c>check_no_inputs_seen_before</c> so the receiver can reject probing attempts and
/// re-entrant payjoins that replay a prior proposal's inputs.
///
/// Retention policy: seen inputs are persisted forever and rejected forever, deliberately including
/// inputs from sessions that never completed - a failed attempt is exactly what a probing adversary
/// produces, and forgetting it would let the same inputs probe again. This mirrors payjoin-cli's
/// seen-inputs database. The cost is one small row per inspected input and permanent rejection of
/// an input that once appeared in any original proposal; senders retrying a failed payjoin are
/// expected to do so with a fresh original (their wallet re-selects or the previous original was
/// broadcast, spending those inputs).
/// </summary>
public sealed class PayjoinSeenInputStore
{
    private readonly PayjoinPluginDbContextFactory _pluginDbContextFactory;

    internal PayjoinSeenInputStore(PayjoinPluginDbContextFactory pluginDbContextFactory)
    {
        ArgumentNullException.ThrowIfNull(pluginDbContextFactory);
        _pluginDbContextFactory = pluginDbContextFactory;
    }

    internal T ExecuteSeenInputTransition<T>(
        string invoiceId,
        Func<IsOutputKnown, JsonReceiverSessionPersister, T> executeTransition)
        where T : class, IDisposable
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invoiceId);
        ArgumentNullException.ThrowIfNull(executeTransition);

        using var context = _pluginDbContextFactory.CreateContext();
        var callback = new StagedInputsSeenCallback(context);
        var persister = new SeenInputTransitionPersister(context, invoiceId);
        var nextState = executeTransition(callback, persister);
        ArgumentNullException.ThrowIfNull(nextState);
        if (persister.HasCommitted)
        {
            return nextState;
        }

        nextState.Dispose();
        throw new InvalidOperationException("The seen-input transition returned without persisting its session event.");
    }

    private sealed class StagedInputsSeenCallback : IsOutputKnown
    {
        private readonly PayjoinPluginDbContext _context;
        private readonly HashSet<SeenOutPoint> _stagedInputs = [];

        public StagedInputsSeenCallback(PayjoinPluginDbContext context)
        {
            _context = context;
        }

        public bool Callback(global::Payjoin.OutPoint outpoint)
        {
            var candidate = new SeenOutPoint(outpoint.Txid, checked((long)outpoint.Vout));
            if (_stagedInputs.Contains(candidate))
            {
                return true;
            }

            var alreadyPresent = _context.ReceiverSeenInputs
                .AsNoTracking()
                .Any(x => x.TransactionId == candidate.TransactionId && x.OutputIndex == candidate.OutputIndex);
            if (alreadyPresent)
            {
                return true;
            }

            _context.ReceiverSeenInputs.Add(new PayjoinReceiverSeenInputData
            {
                TransactionId = candidate.TransactionId,
                OutputIndex = candidate.OutputIndex,
                SeenAt = DateTimeOffset.UtcNow
            });
            _stagedInputs.Add(candidate);
            return false;
        }
    }

    private sealed class SeenInputTransitionPersister : JsonReceiverSessionPersister
    {
        private readonly PayjoinPluginDbContext _context;
        private readonly string _invoiceId;

        public SeenInputTransitionPersister(PayjoinPluginDbContext context, string invoiceId)
        {
            _context = context;
            _invoiceId = invoiceId;
        }

        public bool HasCommitted { get; private set; }

        public void Save(string @event)
        {
            if (HasCommitted)
            {
                throw new InvalidOperationException("The seen-input transition has already been persisted.");
            }

            var session = _context.ReceiverSessions.SingleOrDefault(x => x.InvoiceId == _invoiceId)
                ?? throw new InvalidOperationException($"Payjoin receiver session {_invoiceId} is no longer active.");
            var createdAt = DateTimeOffset.UtcNow;
            var lastSequence = _context.ReceiverSessionEvents
                .Where(x => x.InvoiceId == _invoiceId)
                .Select(x => (int?)x.Sequence)
                .Max() ?? 0;

            session.UpdatedAt = createdAt;
            _context.ReceiverSessionEvents.Add(new PayjoinReceiverSessionEventData
            {
                InvoiceId = _invoiceId,
                Sequence = checked(lastSequence + 1),
                Event = @event,
                CreatedAt = createdAt
            });

            _context.SaveChanges();
            HasCommitted = true;
        }

        public string[] Load()
        {
            return _context.ReceiverSessionEvents
                .AsNoTracking()
                .Where(x => x.InvoiceId == _invoiceId)
                .OrderBy(x => x.Sequence)
                .Select(x => x.Event)
                .ToArray();
        }

        public void Close()
        {
        }
    }

    private readonly record struct SeenOutPoint(string TransactionId, long OutputIndex);
}
