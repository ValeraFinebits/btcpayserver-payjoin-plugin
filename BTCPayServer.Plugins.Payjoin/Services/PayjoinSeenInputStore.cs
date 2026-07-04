using BTCPayServer.Plugins.Payjoin.Data;
using Microsoft.EntityFrameworkCore;
using System;
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
    private readonly IPayjoinUniqueConstraintViolationDetector _uniqueConstraintViolationDetector;

    internal PayjoinSeenInputStore(
        PayjoinPluginDbContextFactory pluginDbContextFactory,
        IPayjoinUniqueConstraintViolationDetector uniqueConstraintViolationDetector)
    {
        ArgumentNullException.ThrowIfNull(pluginDbContextFactory);
        ArgumentNullException.ThrowIfNull(uniqueConstraintViolationDetector);
        _pluginDbContextFactory = pluginDbContextFactory;
        _uniqueConstraintViolationDetector = uniqueConstraintViolationDetector;
    }

    /// <summary>
    /// Records the outpoint as seen and reports whether it had already been recorded before this call.
    /// Returns <c>true</c> when the outpoint was already present (i.e. seen before).
    /// </summary>
    public bool MarkSeenAndWasPresent(string transactionId, long outputIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);

        using var context = _pluginDbContextFactory.CreateContext();
        var alreadyPresent = context.ReceiverSeenInputs
            .AsNoTracking()
            .Any(x => x.TransactionId == transactionId && x.OutputIndex == outputIndex);
        if (alreadyPresent)
        {
            return true;
        }

        context.ReceiverSeenInputs.Add(new PayjoinReceiverSeenInputData
        {
            TransactionId = transactionId,
            OutputIndex = outputIndex,
            SeenAt = DateTimeOffset.UtcNow
        });

        try
        {
            context.SaveChanges();
            return false;
        }
        catch (DbUpdateException ex) when (IsSeenInputConflict(ex))
        {
            // A concurrent session recorded the same outpoint first; treat it as seen before.
            return true;
        }
    }

    private bool IsSeenInputConflict(DbUpdateException exception)
    {
        return _uniqueConstraintViolationDetector.IsUniqueConstraintViolation(exception, PayjoinPluginDbSchema.ReceiverSeenInputsOutPointIndex);
    }
}
