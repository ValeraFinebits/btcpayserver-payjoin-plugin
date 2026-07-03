using BTCPayServer.Plugins.Payjoin.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

/// <summary>
/// Store-facing view over the accounting bridge lifecycle: settlement records that stopped before
/// reconciliation completed, and the ability to give one another reconciliation window.
/// </summary>
public sealed class PayjoinBridgeAttentionService
{
    private readonly IPayjoinAccountingBridgeService _accountingBridgeService;

    internal PayjoinBridgeAttentionService(IPayjoinAccountingBridgeService accountingBridgeService)
    {
        _accountingBridgeService = accountingBridgeService;
    }

    public async Task<IReadOnlyCollection<PayjoinBridgeAttentionItem>> GetRequiringAttentionAsync(string storeId, CancellationToken cancellationToken)
    {
        var bridges = await _accountingBridgeService.GetRequiringAttentionAsync(storeId, cancellationToken).ConfigureAwait(false);
        return bridges
            .Select(bridge => new PayjoinBridgeAttentionItem(
                bridge.InvoiceId,
                bridge.Status == PayjoinAccountingBridgeStatus.Failed,
                bridge.ExpectedFinalTransactionId,
                bridge.FailureMessage,
                bridge.UpdatedAt))
            .ToArray();
    }

    public async Task<bool> TryRetryAsync(string invoiceId, string storeId, CancellationToken cancellationToken)
    {
        var bridge = await _accountingBridgeService.TryRetryAsync(invoiceId, storeId, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        return bridge is not null;
    }
}

public sealed record PayjoinBridgeAttentionItem(
    string InvoiceId,
    bool IsFailed,
    string? ExpectedFinalTransactionId,
    string? FailureMessage,
    DateTimeOffset UpdatedAt);
