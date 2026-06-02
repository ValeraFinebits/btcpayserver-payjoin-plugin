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

    Task<PayjoinAccountingBridgeState?> SetSettlementScriptAsync(string invoiceId, string settlementScript, CancellationToken cancellationToken);

    Task<PayjoinAccountingBridgeState?> SetExpectedFinalTransactionAsync(string invoiceId, string expectedFinalTransactionId, long? expectedFinalOutputIndex, long? expectedFinalValueSats, CancellationToken cancellationToken);

    Task<PayjoinAccountingBridgeState?> MarkReconciledAsync(string invoiceId, string? expectedFinalTransactionId, long? expectedFinalOutputIndex, long? expectedFinalValueSats, DateTimeOffset reconciledAt, CancellationToken cancellationToken);

    Task<PayjoinAccountingBridgeState?> MarkFailedAsync(string invoiceId, string failureMessage, CancellationToken cancellationToken);

    Task<int> ExpirePendingAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

internal sealed class PayjoinAccountingBridgeService : IPayjoinAccountingBridgeService
{
    private readonly PayjoinPluginDbContextFactory _dbContextFactory;
    private readonly IPayjoinUniqueConstraintViolationDetector _uniqueConstraintViolationDetector;

    public PayjoinAccountingBridgeService(
        PayjoinPluginDbContextFactory dbContextFactory,
        IPayjoinUniqueConstraintViolationDetector uniqueConstraintViolationDetector)
    {
        _dbContextFactory = dbContextFactory;
        _uniqueConstraintViolationDetector = uniqueConstraintViolationDetector;
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
        var bridges = await context.AccountingBridges
            .AsNoTracking()
            .Where(x => (x.Status == PayjoinAccountingBridgeStatus.PendingFallback || x.Status == PayjoinAccountingBridgeStatus.PendingFinalTransaction) &&
                        (x.ExpiresAt == null || x.ExpiresAt > now))
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

    public Task<PayjoinAccountingBridgeState?> SetSettlementScriptAsync(string invoiceId, string settlementScript, CancellationToken cancellationToken)
    {
        return UpdateAsync(
            invoiceId,
            bridge =>
            {
                bridge.SettlementScript = settlementScript;
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

    public async Task<int> ExpirePendingAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var context = _dbContextFactory.CreateContext();
        var expired = await context.AccountingBridges
            .Where(x => (x.Status == PayjoinAccountingBridgeStatus.PendingFallback || x.Status == PayjoinAccountingBridgeStatus.PendingFinalTransaction) &&
                        x.ExpiresAt != null &&
                        x.ExpiresAt <= now)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (expired.Length == 0)
        {
            return 0;
        }

        foreach (var bridge in expired)
        {
            bridge.Status = PayjoinAccountingBridgeStatus.Expired;
            bridge.UpdatedAt = now;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return expired.Length;
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
