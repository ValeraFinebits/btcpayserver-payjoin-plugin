using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

public sealed class PayjoinReceiverPoller : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogPayjoinReceiverPollingFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(1, nameof(LogPayjoinReceiverPollingFailed)),
            "Payjoin receiver polling failed.");
    private static readonly Action<ILogger, string, Exception?> LogPayjoinAccountingReconciliationFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(2, nameof(LogPayjoinAccountingReconciliationFailed)),
            "Payjoin accounting reconciliation failed for {InvoiceId}.");
    private static readonly Action<ILogger, string, Exception?> LogPayjoinAccountingBridgePendingWithoutReconciliation =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(3, nameof(LogPayjoinAccountingBridgePendingWithoutReconciliation)),
            "Payjoin accounting bridge remains pending for {InvoiceId} because reconciliation produced no payment update.");
    private readonly PayjoinReceiverSessionStore _sessionStore;
    private readonly IPayjoinReceiverSessionProcessor _sessionProcessor;
    private readonly IPayjoinAccountingBridgeService _accountingBridgeService;
    private readonly IPayjoinAccountingPaymentService _accountingPaymentService;
    private readonly ILogger<PayjoinReceiverPoller> _logger;

    internal PayjoinReceiverPoller(
        PayjoinReceiverSessionStore sessionStore,
        IPayjoinReceiverSessionProcessor sessionProcessor,
        IPayjoinAccountingBridgeService accountingBridgeService,
        IPayjoinAccountingPaymentService accountingPaymentService,
        ILogger<PayjoinReceiverPoller> logger)
    {
        _sessionStore = sessionStore;
        _sessionProcessor = sessionProcessor;
        _accountingBridgeService = accountingBridgeService;
        _accountingPaymentService = accountingPaymentService;
        _logger = logger;
    }

    internal async Task ProcessTickOnceAsync(CancellationToken stoppingToken)
    {
        var now = DateTimeOffset.UtcNow;
        _sessionStore.CleanupExpiredInputReservations(now);
        await _sessionProcessor.ProcessTickAsync(stoppingToken).ConfigureAwait(false);
        await ReconcilePendingBridgesAsync(now, stoppingToken).ConfigureAwait(false);
    }

    private async Task ReconcilePendingBridgesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await _accountingBridgeService.ExpirePendingAsync(now, cancellationToken).ConfigureAwait(false);
        var bridges = await _accountingBridgeService.GetPendingAsync(now, cancellationToken).ConfigureAwait(false);
        foreach (var bridge in bridges)
        {
            if (string.IsNullOrWhiteSpace(bridge.ExpectedFinalTransactionId))
            {
                continue;
            }

            try
            {
                var payment = await _accountingPaymentService.ReconcileWithFinalTransactionAsync(bridge, cancellationToken).ConfigureAwait(false);
                if (payment is null)
                {
                    LogPayjoinAccountingBridgePendingWithoutReconciliation(_logger, bridge.InvoiceId, null);
                    continue;
                }

                await _accountingBridgeService.MarkReconciledAsync(
                    bridge.InvoiceId,
                    bridge.ExpectedFinalTransactionId,
                    bridge.ExpectedFinalOutputIndex,
                    bridge.ExpectedFinalValueSats,
                    DateTimeOffset.UtcNow,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (PayjoinAccountingReconciliationDataException ex)
            {
                LogPayjoinAccountingReconciliationFailed(_logger, bridge.InvoiceId, ex);
                await _accountingBridgeService.MarkFailedAsync(bridge.InvoiceId, ex.Message, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                LogPayjoinAccountingReconciliationFailed(_logger, bridge.InvoiceId, ex);
            }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Plugin background execution is an isolation boundary and must not crash the host process.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await ProcessTickOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogPayjoinReceiverPollingFailed(_logger, ex);
            }
        }
    }
}
