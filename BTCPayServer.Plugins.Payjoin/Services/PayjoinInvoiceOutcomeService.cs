using BTCPayServer.Models.InvoicingModels;
using BTCPayServer.Plugins.Payjoin.Data;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal sealed record PayjoinInvoiceOutcome(
    PayjoinAccountingBridgeStatus Status,
    string StoreId,
    string? SettlementTransactionId);

internal sealed class PayjoinInvoiceOutcomeService
{
    private static readonly Action<ILogger, string, Exception?> LogOutcomeLookupFailed =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(1, nameof(LogOutcomeLookupFailed)),
            "Failed to read the Async Payjoin settlement record for {InvoiceId}; the invoice page is rendered without it.");

    private readonly IPayjoinAccountingBridgeService _accountingBridgeService;
    private readonly ILogger<PayjoinInvoiceOutcomeService> _logger;

    public PayjoinInvoiceOutcomeService(
        IPayjoinAccountingBridgeService accountingBridgeService,
        ILogger<PayjoinInvoiceOutcomeService> logger)
    {
        _accountingBridgeService = accountingBridgeService;
        _logger = logger;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Reporting the Async Payjoin outcome is cosmetic and must never break BTCPay's invoice page.")]
    public async Task<PayjoinInvoiceOutcome?> TryGetAsync(InvoiceDetailsModel? invoice, CancellationToken cancellationToken)
    {
        var invoiceId = ResolveInvoiceId(invoice?.Id, invoice?.Entity?.Id);
        if (invoiceId is null)
        {
            return null;
        }

        try
        {
            var bridge = await _accountingBridgeService.TryGetByInvoiceIdAsync(invoiceId, cancellationToken).ConfigureAwait(false);
            return bridge is null
                ? null
                : new PayjoinInvoiceOutcome(bridge.Status, bridge.StoreId, bridge.ExpectedFinalTransactionId);
        }
        catch (Exception ex)
        {
            LogOutcomeLookupFailed(_logger, invoiceId, ex);
            return null;
        }
    }

    internal static string? ResolveInvoiceId(string? modelInvoiceId, string? entityInvoiceId)
    {
        if (!string.IsNullOrEmpty(modelInvoiceId))
        {
            return modelInvoiceId;
        }

        return string.IsNullOrEmpty(entityInvoiceId) ? null : entityInvoiceId;
    }
}
