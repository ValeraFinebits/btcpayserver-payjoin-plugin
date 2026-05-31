using System;

namespace BTCPayServer.Plugins.Payjoin.Data;

internal enum PayjoinAccountingBridgeStatus
{
    PendingFallback = 0,
    PendingFinalTransaction = 1,
    Reconciled = 2,
    Failed = 3,
    Expired = 4
}

internal sealed class PayjoinAccountingBridgeData
{
    public long Id { get; set; }

    public string InvoiceId { get; set; } = null!;

    public string StoreId { get; set; } = null!;

    public string CryptoCode { get; set; } = null!;

    public string PaymentMethodId { get; set; } = null!;

    public string? FallbackTransactionId { get; set; }

    public long? FallbackOutputIndex { get; set; }

    public long? FallbackValueSats { get; set; }

    public long? EffectiveInvoiceValueSats { get; set; }

    public string? SettlementScript { get; set; }

    public string? ExpectedFinalTransactionId { get; set; }

    public long? ExpectedFinalOutputIndex { get; set; }

    public long? ExpectedFinalValueSats { get; set; }

    public string? FailureMessage { get; set; }

    public PayjoinAccountingBridgeStatus Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? ReconciledAt { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }
}
