using BTCPayServer.Plugins.Payjoin.Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal enum PayjoinAttentionRecordSeedKind
{
    Failed,
    Expired
}

internal sealed record SeedPayjoinAttentionRecordRequest(
    string InvoiceId,
    string StoreId,
    string CryptoCode,
    string PaymentMethodId,
    PayjoinAttentionRecordSeedKind Kind,
    DateTimeOffset SeededAt);

internal interface IPayjoinAttentionRecordSeeder
{
    Task<PayjoinAccountingBridgeStatus?> TrySeedAttentionRecordAsync(
        SeedPayjoinAttentionRecordRequest request,
        CancellationToken cancellationToken);
}

internal sealed class PayjoinAttentionRecordSeeder : IPayjoinAttentionRecordSeeder
{
    private static readonly TimeSpan SeededFailedLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan SeededExpiredAge = PayjoinAccountingBridgeService.ArmedBridgeGracePeriod + TimeSpan.FromMinutes(1);
    private const string SeedMarker = "SEEDED:";
    private const string SeededExpectedFinalTransactionId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1";

    private readonly PayjoinPluginDbContextFactory _dbContextFactory;
    private readonly IPayjoinUniqueConstraintViolationDetector _uniqueConstraintViolationDetector;
    private readonly PayjoinSessionBuildLock _sessionBuildLock;

    public PayjoinAttentionRecordSeeder(
        PayjoinPluginDbContextFactory dbContextFactory,
        IPayjoinUniqueConstraintViolationDetector uniqueConstraintViolationDetector,
        PayjoinSessionBuildLock sessionBuildLock)
    {
        _dbContextFactory = dbContextFactory;
        _uniqueConstraintViolationDetector = uniqueConstraintViolationDetector;
        _sessionBuildLock = sessionBuildLock;
    }

    public async Task<PayjoinAccountingBridgeStatus?> TrySeedAttentionRecordAsync(
        SeedPayjoinAttentionRecordRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var sessionBuildLock = await _sessionBuildLock
            .AcquireAsync(request.InvoiceId, cancellationToken)
            .ConfigureAwait(false);
        using var context = _dbContextFactory.CreateContext();
        if (await context.AccountingBridges.AnyAsync(x => x.InvoiceId == request.InvoiceId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var isExpired = request.Kind == PayjoinAttentionRecordSeedKind.Expired;
        var expiresAt = isExpired
            ? request.SeededAt - SeededExpiredAge
            : request.SeededAt + SeededFailedLifetime;
        var bridge = new PayjoinAccountingBridgeData
        {
            InvoiceId = request.InvoiceId,
            StoreId = request.StoreId,
            CryptoCode = request.CryptoCode,
            PaymentMethodId = request.PaymentMethodId,
            ExpectedFinalTransactionId = isExpired ? SeededExpectedFinalTransactionId : null,
            ExpectedFinalOutputIndex = isExpired ? 0 : null,
            ExpectedFinalValueSats = isExpired ? 1000 : null,
            FailureMessage = isExpired
                ? $"{SeedMarker} armed settlement record exceeded its reconciliation window"
                : $"{SeedMarker} reconciliation data did not match the expected settlement",
            Status = isExpired
                ? PayjoinAccountingBridgeStatus.Expired
                : PayjoinAccountingBridgeStatus.Failed,
            CreatedAt = isExpired ? expiresAt - TimeSpan.FromMinutes(1) : request.SeededAt,
            UpdatedAt = request.SeededAt,
            ExpiresAt = expiresAt
        };
        context.AccountingBridges.Add(bridge);

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return bridge.Status;
        }
        catch (DbUpdateException ex) when (_uniqueConstraintViolationDetector.IsUniqueConstraintViolation(ex, PayjoinPluginDbSchema.AccountingBridgesInvoiceIdIndex))
        {
            return null;
        }
    }
}
