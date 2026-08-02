using BTCPayServer.Plugins.Payjoin.Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal sealed record PayjoinAccountingBridgeState(
    long Id,
    string InvoiceId,
    string StoreId,
    string CryptoCode,
    string PaymentMethodId,
    string? FallbackTransactionId,
    long? FallbackOutputIndex,
    long? FallbackValueSats,
    long? EffectiveInvoiceValueSats,
    string? SettlementScript,
    string? ExpectedFinalTransactionId,
    long? ExpectedFinalOutputIndex,
    long? ExpectedFinalValueSats,
    string? FailureMessage,
    PayjoinAccountingBridgeStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ReconciledAt,
    DateTimeOffset? ExpiresAt)
{
    public bool HasExpectedFinalOutputIndex => ExpectedFinalOutputIndex.HasValue;

    public bool HasEffectiveInvoiceValue => EffectiveInvoiceValueSats.HasValue;
}

internal sealed record PayjoinAccountingBridgeAttentionResult(
    IReadOnlyList<PayjoinAccountingBridgeState> Bridges,
    int TotalCount);

internal sealed record CreatePayjoinAccountingBridgeRequest(
    string InvoiceId,
    string StoreId,
    string CryptoCode,
    string PaymentMethodId,
    DateTimeOffset? ExpiresAt,
    string? FallbackTransactionId = null,
    long? FallbackOutputIndex = null,
    long? FallbackValueSats = null,
    long? EffectiveInvoiceValueSats = null,
    string? SettlementScript = null,
    string? ExpectedFinalTransactionId = null,
    long? ExpectedFinalOutputIndex = null,
    long? ExpectedFinalValueSats = null);

internal interface IPayjoinAccountingBridgeService
{
    Task<PayjoinAccountingBridgeState> CreateOrGetAsync(CreatePayjoinAccountingBridgeRequest request, CancellationToken cancellationToken);

    Task<PayjoinAccountingBridgeState?> TryGetByInvoiceIdAsync(string invoiceId, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<PayjoinAccountingBridgeState>> GetPendingAsync(DateTimeOffset now, CancellationToken cancellationToken);

    Task<PayjoinAccountingBridgeState?> AttachFallbackAsync(string invoiceId, string fallbackTransactionId, long fallbackOutputIndex, long fallbackValueSats, long effectiveInvoiceValueSats, string? settlementScript, CancellationToken cancellationToken);

    Task<PayjoinAccountingBridgeState?> SetExpectedFinalTransactionAsync(string invoiceId, string expectedFinalTransactionId, long? expectedFinalOutputIndex, long? expectedFinalValueSats, CancellationToken cancellationToken);

    Task<PayjoinAccountingBridgeState?> MarkReconciledAsync(string invoiceId, string? expectedFinalTransactionId, long? expectedFinalOutputIndex, long? expectedFinalValueSats, DateTimeOffset reconciledAt, CancellationToken cancellationToken);

    Task<PayjoinAccountingBridgeState?> MarkFailedAsync(string invoiceId, string failureMessage, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<PayjoinAccountingBridgeState>> ExpirePendingAsync(DateTimeOffset now, CancellationToken cancellationToken);

    Task<PayjoinAccountingBridgeAttentionResult> GetRequiringAttentionAsync(string storeId, CancellationToken cancellationToken);

    Task<PayjoinAccountingBridgeState?> TryRetryAsync(string invoiceId, string storeId, DateTimeOffset now, CancellationToken cancellationToken);

    Task<PayjoinAccountingBridgeState?> ResetForNewSessionAsync(string invoiceId, long? effectiveInvoiceValueSats, DateTimeOffset? expiresAt, CancellationToken cancellationToken);
}

internal sealed class PayjoinAccountingBridgeService : IPayjoinAccountingBridgeService
{
    // A bridge that already knows its expected final transaction represents a proposal the sender may
    // broadcast at any moment, so it outlives the invoice monitoring deadline by this grace period.
    // That absorbs confirmations and transient reconciliation failures landing near the deadline
    // instead of retiring the bridge while its settlement can still be recorded.
    internal static readonly TimeSpan ArmedBridgeGracePeriod = TimeSpan.FromHours(6);

    // The attention list is bounded so one store cannot render an unbounded table; the total count
    // travels alongside it so the UI can tell operators when older records are not shown.
    internal const int AttentionListLimit = 50;

    private readonly PayjoinPluginDbContextFactory _dbContextFactory;
    private readonly IPayjoinUniqueConstraintViolationDetector _uniqueConstraintViolationDetector;
    private readonly PayjoinSessionBuildLock _sessionBuildLock;

    public PayjoinAccountingBridgeService(
        PayjoinPluginDbContextFactory dbContextFactory,
        IPayjoinUniqueConstraintViolationDetector uniqueConstraintViolationDetector,
        PayjoinSessionBuildLock sessionBuildLock)
    {
        _dbContextFactory = dbContextFactory;
        _uniqueConstraintViolationDetector = uniqueConstraintViolationDetector;
        _sessionBuildLock = sessionBuildLock;
    }

    public async Task<PayjoinAccountingBridgeState> CreateOrGetAsync(CreatePayjoinAccountingBridgeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var context = _dbContextFactory.CreateContext();
        var existing = await TryLoadByInvoiceIdAsync(context, request.InvoiceId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return ToState(existing);
        }

        var now = DateTimeOffset.UtcNow;
        var bridge = new PayjoinAccountingBridgeData
        {
            InvoiceId = request.InvoiceId,
            StoreId = request.StoreId,
            CryptoCode = request.CryptoCode,
            PaymentMethodId = request.PaymentMethodId,
            FallbackTransactionId = request.FallbackTransactionId,
            FallbackOutputIndex = request.FallbackOutputIndex,
            FallbackValueSats = request.FallbackValueSats,
            EffectiveInvoiceValueSats = request.EffectiveInvoiceValueSats,
            SettlementScript = request.SettlementScript,
            ExpectedFinalTransactionId = request.ExpectedFinalTransactionId,
            ExpectedFinalOutputIndex = request.ExpectedFinalOutputIndex,
            ExpectedFinalValueSats = request.ExpectedFinalValueSats,
            Status = request.ExpectedFinalTransactionId is null
                ? PayjoinAccountingBridgeStatus.PendingFallback
                : PayjoinAccountingBridgeStatus.PendingFinalTransaction,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = request.ExpiresAt
        };
        context.AccountingBridges.Add(bridge);
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return ToState(bridge);
        }
        catch (DbUpdateException ex) when (IsInvoiceBridgeConflict(ex))
        {
            using var recoveryContext = _dbContextFactory.CreateContext();
            var recoveredBridge = await TryLoadByInvoiceIdAsync(recoveryContext, request.InvoiceId, cancellationToken).ConfigureAwait(false);
            if (recoveredBridge is not null)
            {
                return ToState(recoveredBridge);
            }

            throw;
        }
    }

    public async Task<PayjoinAccountingBridgeState?> TryGetByInvoiceIdAsync(string invoiceId, CancellationToken cancellationToken)
    {
        using var context = _dbContextFactory.CreateContext();
        var bridge = await context.AccountingBridges
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(x => x.InvoiceId == invoiceId, cancellationToken)
            .ConfigureAwait(false);
        return bridge is null ? null : ToState(bridge);
    }

    public async Task<IReadOnlyCollection<PayjoinAccountingBridgeState>> GetPendingAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var context = _dbContextFactory.CreateContext();
        var armedDeadline = now - ArmedBridgeGracePeriod;
        var bridges = await context.AccountingBridges
            .AsNoTracking()
            .Where(x => (x.Status == PayjoinAccountingBridgeStatus.PendingFallback || x.Status == PayjoinAccountingBridgeStatus.PendingFinalTransaction) &&
                        (x.ExpiresAt == null || (x.ExpectedFinalTransactionId == null ? x.ExpiresAt > now : x.ExpiresAt > armedDeadline)))
            .OrderBy(x => x.CreatedAt)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return bridges.Select(ToState).ToArray();
    }

    public Task<PayjoinAccountingBridgeState?> AttachFallbackAsync(string invoiceId, string fallbackTransactionId, long fallbackOutputIndex, long fallbackValueSats, long effectiveInvoiceValueSats, string? settlementScript, CancellationToken cancellationToken)
    {
        return UpdateAsync(
            invoiceId,
            bridge =>
            {
                bridge.FallbackTransactionId = fallbackTransactionId;
                bridge.FallbackOutputIndex = fallbackOutputIndex;
                bridge.FallbackValueSats = fallbackValueSats;
                bridge.EffectiveInvoiceValueSats = effectiveInvoiceValueSats;
                bridge.SettlementScript = settlementScript ?? bridge.SettlementScript;
                if (bridge.Status == PayjoinAccountingBridgeStatus.PendingFallback)
                {
                    bridge.Status = PayjoinAccountingBridgeStatus.PendingFinalTransaction;
                }
            },
            cancellationToken);
    }

    public Task<PayjoinAccountingBridgeState?> SetExpectedFinalTransactionAsync(string invoiceId, string expectedFinalTransactionId, long? expectedFinalOutputIndex, long? expectedFinalValueSats, CancellationToken cancellationToken)
    {
        return UpdateAsync(
            invoiceId,
            bridge =>
            {
                bridge.ExpectedFinalTransactionId = expectedFinalTransactionId;
                bridge.ExpectedFinalOutputIndex = expectedFinalOutputIndex;
                bridge.ExpectedFinalValueSats = expectedFinalValueSats;
                if (bridge.Status == PayjoinAccountingBridgeStatus.PendingFallback)
                {
                    bridge.Status = PayjoinAccountingBridgeStatus.PendingFinalTransaction;
                }
            },
            cancellationToken);
    }

    public Task<PayjoinAccountingBridgeState?> MarkReconciledAsync(string invoiceId, string? expectedFinalTransactionId, long? expectedFinalOutputIndex, long? expectedFinalValueSats, DateTimeOffset reconciledAt, CancellationToken cancellationToken)
    {
        return UpdateAsync(
            invoiceId,
            bridge =>
            {
                bridge.ExpectedFinalTransactionId = expectedFinalTransactionId ?? bridge.ExpectedFinalTransactionId;
                bridge.ExpectedFinalOutputIndex = expectedFinalOutputIndex ?? bridge.ExpectedFinalOutputIndex;
                bridge.ExpectedFinalValueSats = expectedFinalValueSats ?? bridge.ExpectedFinalValueSats;
                bridge.Status = PayjoinAccountingBridgeStatus.Reconciled;
                bridge.ReconciledAt = reconciledAt;
                bridge.FailureMessage = null;
            },
            cancellationToken);
    }

    public Task<PayjoinAccountingBridgeState?> MarkFailedAsync(string invoiceId, string failureMessage, CancellationToken cancellationToken)
    {
        return UpdateAsync(
            invoiceId,
            bridge =>
            {
                bridge.Status = PayjoinAccountingBridgeStatus.Failed;
                bridge.FailureMessage = failureMessage;
            },
            cancellationToken);
    }

    public async Task<PayjoinAccountingBridgeState?> ResetForNewSessionAsync(string invoiceId, long? effectiveInvoiceValueSats, DateTimeOffset? expiresAt, CancellationToken cancellationToken)
    {
        using var context = _dbContextFactory.CreateContext();
        var bridge = await TryLoadByInvoiceIdAsync(context, invoiceId, cancellationToken).ConfigureAwait(false);
        if (bridge is null)
        {
            return null;
        }

        // A freshly created receiver session produces its own fallback, settlement script and final
        // transaction, so tracking data from a previous session on the same invoice no longer applies:
        // the fallback-attach guard would hold on to the old fallback while later writes would overwrite
        // the rest, leaving the record describing two different sessions at once. Reconciled records are
        // final, and failed or expired records that already awaited a final transaction stay untouched
        // for operator review.
        //
        // A pending bridge that is already armed is the exception: its expected final transaction
        // describes a signed proposal the previous session handed to the sender, which can still
        // confirm, and that expectation is the only thing that makes the settlement creditable (the
        // settlement output is not an invoice address the platform tracks on its own). The old
        // accounting flow therefore stays live through session recreation, and the new session's own
        // writes take the record over stage by stage: attaching its fallback replaces the fallback
        // data, committing outputs replaces the settlement script, and finalizing its proposal
        // replaces the expected final transaction.
        var isResettablePending = (bridge.Status is PayjoinAccountingBridgeStatus.PendingFallback or PayjoinAccountingBridgeStatus.PendingFinalTransaction) &&
                                  bridge.ExpectedFinalTransactionId is null;
        var isResettableExpired = bridge.Status == PayjoinAccountingBridgeStatus.Expired && bridge.ExpectedFinalTransactionId is null;
        if (!isResettablePending && !isResettableExpired)
        {
            return ToState(bridge);
        }

        var hasPriorSessionData = bridge.FallbackTransactionId is not null ||
                                  bridge.SettlementScript is not null;
        if (!hasPriorSessionData && bridge.Status != PayjoinAccountingBridgeStatus.Expired)
        {
            return ToState(bridge);
        }

        bridge.FallbackTransactionId = null;
        bridge.FallbackOutputIndex = null;
        bridge.FallbackValueSats = null;
        bridge.SettlementScript = null;
        bridge.ExpectedFinalTransactionId = null;
        bridge.ExpectedFinalOutputIndex = null;
        bridge.ExpectedFinalValueSats = null;
        bridge.EffectiveInvoiceValueSats = effectiveInvoiceValueSats ?? bridge.EffectiveInvoiceValueSats;
        bridge.FailureMessage = null;
        bridge.Status = PayjoinAccountingBridgeStatus.PendingFallback;
        bridge.ExpiresAt = expiresAt ?? bridge.ExpiresAt;
        bridge.UpdatedAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToState(bridge);
    }

    public async Task<IReadOnlyCollection<PayjoinAccountingBridgeState>> ExpirePendingAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var armedDeadline = now - ArmedBridgeGracePeriod;
        string[] candidateInvoiceIds;
        using (var context = _dbContextFactory.CreateContext())
        {
            candidateInvoiceIds = await context.AccountingBridges
                .AsNoTracking()
                .Where(x => (x.Status == PayjoinAccountingBridgeStatus.PendingFallback || x.Status == PayjoinAccountingBridgeStatus.PendingFinalTransaction) &&
                            x.ExpiresAt != null &&
                            (x.ExpectedFinalTransactionId == null ? x.ExpiresAt <= now : x.ExpiresAt <= armedDeadline))
                .Select(x => x.InvoiceId)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        if (candidateInvoiceIds.Length == 0)
        {
            return [];
        }

        // Expiry writes race session recreation: ResetForNewSessionAsync revives a bridge under the
        // invoice's session build lock, and an expiry write from a snapshot taken before that revival
        // would mark the revived bridge Expired, cutting it off from reconciliation. Taking the same
        // per-invoice lock and re-checking the deadline inside it makes the interleaving safe in both
        // orders: if the revival won, the re-check sees the new deadline and skips; if expiry won, the
        // reset revives an Expired bridge, which is its designed path.
        var expired = new List<PayjoinAccountingBridgeState>(candidateInvoiceIds.Length);
        foreach (var invoiceId in candidateInvoiceIds)
        {
            using var sessionBuildLock = await _sessionBuildLock.AcquireAsync(invoiceId, cancellationToken).ConfigureAwait(false);
            using var context = _dbContextFactory.CreateContext();
            var bridge = await TryLoadByInvoiceIdAsync(context, invoiceId, cancellationToken).ConfigureAwait(false);
            if (bridge is null ||
                bridge.Status is not (PayjoinAccountingBridgeStatus.PendingFallback or PayjoinAccountingBridgeStatus.PendingFinalTransaction) ||
                bridge.ExpiresAt is null ||
                bridge.ExpiresAt > (bridge.ExpectedFinalTransactionId is null ? now : armedDeadline))
            {
                continue;
            }

            bridge.Status = PayjoinAccountingBridgeStatus.Expired;
            bridge.UpdatedAt = now;
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            expired.Add(ToState(bridge));
        }

        return expired;
    }

    public async Task<PayjoinAccountingBridgeAttentionResult> GetRequiringAttentionAsync(string storeId, CancellationToken cancellationToken)
    {
        using var context = _dbContextFactory.CreateContext();
        var query = context.AccountingBridges
            .AsNoTracking()
            .Where(x => x.StoreId == storeId &&
                        (x.Status == PayjoinAccountingBridgeStatus.Failed ||
                         (x.Status == PayjoinAccountingBridgeStatus.Expired && x.ExpectedFinalTransactionId != null)));
        var totalCount = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var bridges = await query
            .OrderByDescending(x => x.UpdatedAt)
            .Take(AttentionListLimit)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return new PayjoinAccountingBridgeAttentionResult(bridges.Select(ToState).ToArray(), totalCount);
    }

    public async Task<PayjoinAccountingBridgeState?> TryRetryAsync(string invoiceId, string storeId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var context = _dbContextFactory.CreateContext();
        var bridge = await TryLoadByInvoiceIdAsync(context, invoiceId, cancellationToken).ConfigureAwait(false);
        // Retry eligibility matches what GetRequiringAttentionAsync surfaces: failed bridges,
        // and expired bridges that are still armed with an expected final transaction. An
        // expired unarmed bridge has nothing left to reconcile once its invoice monitoring
        // window closed, so it stays terminal.
        if (bridge is null ||
            !string.Equals(bridge.StoreId, storeId, StringComparison.Ordinal) ||
            (bridge.Status != PayjoinAccountingBridgeStatus.Failed &&
             (bridge.Status != PayjoinAccountingBridgeStatus.Expired || bridge.ExpectedFinalTransactionId is null)))
        {
            return null;
        }

        if (bridge.ExpectedFinalTransactionId is null)
        {
            // The grace period exists to outlive the invoice monitoring deadline while a known
            // final transaction confirms. A failed unarmed bridge has no such transaction, so
            // it keeps its original deadline and expires naturally if that deadline has passed.
            bridge.Status = PayjoinAccountingBridgeStatus.PendingFallback;
        }
        else
        {
            bridge.Status = PayjoinAccountingBridgeStatus.PendingFinalTransaction;
            bridge.ExpiresAt = now + ArmedBridgeGracePeriod;
        }

        bridge.FailureMessage = null;
        bridge.UpdatedAt = now;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToState(bridge);
    }

    private async Task<PayjoinAccountingBridgeState?> UpdateAsync(string invoiceId, Action<PayjoinAccountingBridgeData> update, CancellationToken cancellationToken)
    {
        using var context = _dbContextFactory.CreateContext();
        var bridge = await TryLoadByInvoiceIdAsync(context, invoiceId, cancellationToken).ConfigureAwait(false);
        if (bridge is null)
        {
            return null;
        }

        update(bridge);
        bridge.UpdatedAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToState(bridge);
    }

    private bool IsInvoiceBridgeConflict(DbUpdateException exception)
    {
        return _uniqueConstraintViolationDetector.IsUniqueConstraintViolation(exception, PayjoinPluginDbSchema.AccountingBridgesInvoiceIdIndex);
    }

    private static Task<PayjoinAccountingBridgeData?> TryLoadByInvoiceIdAsync(PayjoinPluginDbContext context, string invoiceId, CancellationToken cancellationToken)
    {
        return context.AccountingBridges
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(x => x.InvoiceId == invoiceId, cancellationToken);
    }

    private static PayjoinAccountingBridgeState ToState(PayjoinAccountingBridgeData bridge)
    {
        return new PayjoinAccountingBridgeState(
            bridge.Id,
            bridge.InvoiceId,
            bridge.StoreId,
            bridge.CryptoCode,
            bridge.PaymentMethodId,
            bridge.FallbackTransactionId,
            bridge.FallbackOutputIndex,
            bridge.FallbackValueSats,
            bridge.EffectiveInvoiceValueSats,
            bridge.SettlementScript,
            bridge.ExpectedFinalTransactionId,
            bridge.ExpectedFinalOutputIndex,
            bridge.ExpectedFinalValueSats,
            bridge.FailureMessage,
            bridge.Status,
            bridge.CreatedAt,
            bridge.UpdatedAt,
            bridge.ReconciledAt,
            bridge.ExpiresAt);
    }
}
