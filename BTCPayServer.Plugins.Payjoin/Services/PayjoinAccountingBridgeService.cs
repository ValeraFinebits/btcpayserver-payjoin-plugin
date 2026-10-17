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
    public string? SettlementKeyPath { get; init; }
    public bool RetryAllowed { get; init; } = true;

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
    long? ExpectedFinalValueSats = null)
{
    public string? SettlementKeyPath { get; init; }
}

internal interface IPayjoinAccountingBridgeService
{
    Task<PayjoinAccountingBridgeState?> TryGetByInvoiceIdAsync(string invoiceId, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<PayjoinAccountingBridgeState>> GetPendingReconciliationAsync(DateTimeOffset now, CancellationToken cancellationToken);

    Task<PayjoinAccountingBridgeState?> MarkReconciledAsync(string invoiceId, string? expectedFinalTransactionId, long? expectedFinalOutputIndex, long? expectedFinalValueSats, DateTimeOffset reconciledAt, CancellationToken cancellationToken, DateTimeOffset? expectedUpdatedAt = null);

    Task<PayjoinAccountingBridgeState?> MarkFailedAsync(string invoiceId, string failureMessage, CancellationToken cancellationToken, Guid? sessionInstanceId = null, DateTimeOffset? expectedUpdatedAt = null);

    Task<IReadOnlyCollection<PayjoinAccountingBridgeState>> ExpirePendingAsync(DateTimeOffset now, CancellationToken cancellationToken);

    Task<PayjoinAccountingBridgeAttentionResult> GetRequiringAttentionAsync(string storeId, CancellationToken cancellationToken);

    Task<PayjoinAccountingBridgeState?> TryRetryAsync(string invoiceId, string storeId, DateTimeOffset now, CancellationToken cancellationToken);

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
    private readonly PayjoinSessionBuildLock _sessionBuildLock;
    private readonly IPayjoinInvoiceEventPublisher? _invoiceEvents;

    public PayjoinAccountingBridgeService(
        PayjoinPluginDbContextFactory dbContextFactory,
        PayjoinSessionBuildLock sessionBuildLock,
        IPayjoinInvoiceEventPublisher? invoiceEvents = null)
    {
        _dbContextFactory = dbContextFactory;
        _sessionBuildLock = sessionBuildLock;
        _invoiceEvents = invoiceEvents;
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

    public async Task<IReadOnlyCollection<PayjoinAccountingBridgeState>> GetPendingReconciliationAsync(DateTimeOffset now, CancellationToken cancellationToken)
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

    public Task<PayjoinAccountingBridgeState?> MarkReconciledAsync(string invoiceId, string? expectedFinalTransactionId, long? expectedFinalOutputIndex, long? expectedFinalValueSats, DateTimeOffset reconciledAt, CancellationToken cancellationToken, DateTimeOffset? expectedUpdatedAt = null)
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
            cancellationToken, condition: bridge => !expectedUpdatedAt.HasValue || bridge.UpdatedAt == expectedUpdatedAt.Value,
            invoiceEventKind: PayjoinInvoiceEventKind.Reconciled);
    }

    public Task<PayjoinAccountingBridgeState?> MarkFailedAsync(string invoiceId, string failureMessage, CancellationToken cancellationToken, Guid? sessionInstanceId = null, DateTimeOffset? expectedUpdatedAt = null)
    {
        return UpdateAsync(
            invoiceId,
            bridge =>
            {
                bridge.Status = PayjoinAccountingBridgeStatus.Failed;
                bridge.FailureMessage = failureMessage;
            },
            cancellationToken,
            sessionInstanceId: sessionInstanceId, condition: bridge => !expectedUpdatedAt.HasValue || bridge.UpdatedAt == expectedUpdatedAt.Value,
            invoiceEventKind: PayjoinInvoiceEventKind.Failed);
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

        // Recheck each candidate with the session/bridge concurrency tokens in a fresh context.
        var expired = new List<PayjoinAccountingBridgeState>(candidateInvoiceIds.Length);
        foreach (var invoiceId in candidateInvoiceIds)
        {
            using var sessionBuildLock = await _sessionBuildLock.AcquireAsync(invoiceId, cancellationToken).ConfigureAwait(false);
            var bridge = await UpdateAsync(invoiceId, data => data.Status = PayjoinAccountingBridgeStatus.Expired,
                cancellationToken, condition: data =>
                    data.Status is PayjoinAccountingBridgeStatus.PendingFallback or PayjoinAccountingBridgeStatus.PendingFinalTransaction &&
                    data.ExpiresAt is not null && data.ExpiresAt <= (data.ExpectedFinalTransactionId is null ? now : armedDeadline),
                updatedAt: now, invoiceEventKind: PayjoinInvoiceEventKind.Expired).ConfigureAwait(false);
            if (bridge is not null) expired.Add(bridge);

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
        var failedInvoices = bridges.Where(x => x.Status == PayjoinAccountingBridgeStatus.Failed).Select(x => x.InvoiceId).ToArray();
        var heldInvoices = await context.ReceiverSessions.Where(x => failedInvoices.Contains(x.InvoiceId))
            .Select(x => x.InvoiceId).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return new PayjoinAccountingBridgeAttentionResult(bridges.Select(x => ToState(x) with
        {
            RetryAllowed = x.Status != PayjoinAccountingBridgeStatus.Failed || !heldInvoices.Contains(x.InvoiceId, StringComparer.Ordinal)
        }).ToArray(), totalCount);
    }

    public Task<PayjoinAccountingBridgeState?> TryRetryAsync(string invoiceId, string storeId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        return UpdateAsync(invoiceId, bridge =>
        {
            bridge.Status = bridge.ExpectedFinalTransactionId is null
                ? PayjoinAccountingBridgeStatus.PendingFallback : PayjoinAccountingBridgeStatus.PendingFinalTransaction;
            if (bridge.ExpectedFinalTransactionId is not null) bridge.ExpiresAt = now + ArmedBridgeGracePeriod;
            bridge.FailureMessage = null;
        }, cancellationToken, condition: bridge =>
            bridge.StoreId == storeId &&
            (bridge.Status == PayjoinAccountingBridgeStatus.Failed ||
             bridge.Status == PayjoinAccountingBridgeStatus.Expired && bridge.ExpectedFinalTransactionId is not null),
            updatedAt: now, blockHeldFailure: true, invoiceEventKind: PayjoinInvoiceEventKind.RetryRequested);
    }

    private async Task<PayjoinAccountingBridgeState?> UpdateAsync(string invoiceId, Action<PayjoinAccountingBridgeData> update, CancellationToken cancellationToken,
        Guid? sessionInstanceId = null, Func<PayjoinAccountingBridgeData, bool>? condition = null, DateTimeOffset? updatedAt = null, bool blockHeldFailure = false,
        PayjoinInvoiceEventKind? invoiceEventKind = null)
    {
        const int maxAttempts = 3;
        var firstRead = true;
        var expectedAbsence = false;
        DateTimeOffset? expectedBridgeVersion = null;
        for (var attempt = 1; ; attempt++)
        {
            using var context = _dbContextFactory.CreateContext();
            var snapshot = await (from data in context.AccountingBridges
                                  where data.InvoiceId == invoiceId
                                  join row in context.ReceiverSessions on data.InvoiceId equals row.InvoiceId into rows
                                  from row in rows.DefaultIfEmpty()
                                  select new { Bridge = data, Session = row }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var session = snapshot?.Session;
            if (!firstRead && expectedAbsence && session is not null) throw new PayjoinSessionStaleException();
            if (firstRead) { expectedAbsence = session is null; firstRead = false; }
            if (sessionInstanceId.HasValue && (session is null || session.SessionInstanceId != sessionInstanceId.Value)) throw new PayjoinSessionStaleException();
            sessionInstanceId ??= session?.SessionInstanceId;
            var bridge = snapshot?.Bridge;
            if (expectedBridgeVersion.HasValue && bridge?.UpdatedAt != expectedBridgeVersion) throw new PayjoinSessionStaleException();
            expectedBridgeVersion ??= bridge?.UpdatedAt;
            if (bridge is null || condition is not null && !condition(bridge))
            {
                return null;
            }

            if (blockHeldFailure && bridge.Status == PayjoinAccountingBridgeStatus.Failed && session is not null) return null;
            var previousStatus = bridge.Status;
            update(bridge);
            bridge.UpdatedAt = NextWriteTime(bridge, updatedAt ?? DateTimeOffset.UtcNow);
            if (session is not null)
            {
                session.AttemptVersion = checked(session.AttemptVersion + 1);
            }
            try
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException) when (attempt < maxAttempts)
            {
                // SaveChanges rolls back both writes; never reuse its tracked snapshot.
                continue;
            }
            if (invoiceEventKind.HasValue && previousStatus != bridge.Status)
            {
                _invoiceEvents?.Publish(invoiceEventKind.Value, bridge.InvoiceId, bridge.ExpectedFinalTransactionId);
            }
            return ToState(bridge);
        }
    }

    internal static DateTimeOffset NextWriteTime(PayjoinAccountingBridgeData bridge, DateTimeOffset candidate) =>
        candidate.UtcTicks / 10 > bridge.UpdatedAt.UtcTicks / 10 ? candidate : bridge.UpdatedAt.AddTicks(10);

    internal static PayjoinAccountingBridgeState ToState(PayjoinAccountingBridgeData bridge)
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
            bridge.ExpiresAt)
        {
            SettlementKeyPath = bridge.SettlementKeyPath
        };
    }
}
