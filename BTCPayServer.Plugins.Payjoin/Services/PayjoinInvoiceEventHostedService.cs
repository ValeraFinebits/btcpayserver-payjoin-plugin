using BTCPayServer.HostedServices;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal sealed class PayjoinInvoiceEventHostedService : EventHostedServiceBase
{
    private const int MaxWriteAttempts = 3;
    private static readonly TimeSpan WriteRetryDelay = TimeSpan.FromMilliseconds(500);

    private static readonly Action<ILogger, string, Exception?> LogWriteFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, nameof(LogWriteFailed)),
            "Could not write the Payjoin invoice event for {InvoiceId}.");
    private static readonly Action<ILogger, Exception?> LogShutdownDeadlineReached =
        LoggerMessage.Define(LogLevel.Warning, new EventId(2, nameof(LogShutdownDeadlineReached)),
            "Payjoin invoice event processing exceeded the shutdown deadline; remaining events may be lost.");
    private static readonly Action<ILogger, Exception?> LogShutdownFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(3, nameof(LogShutdownFailed)),
            "Payjoin invoice event processing failed while stopping.");

    private readonly IPayjoinInvoiceEventWriter _writer;
    private readonly ILogger<PayjoinInvoiceEventHostedService> _logger;
    private readonly object _stopLock = new();
    private CancellationToken _shutdownDeadline;
    private Task? _stoppingTask;

    public PayjoinInvoiceEventHostedService(
        EventAggregator eventAggregator,
        IPayjoinInvoiceEventWriter writer,
        ILogger<PayjoinInvoiceEventHostedService> logger) : base(eventAggregator, logger)
    {
        _writer = writer;
        _logger = logger;
    }

    protected override void SubscribeToEvents() => Subscribe<PayjoinInvoiceEvent>();

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A failed diagnostic write must not terminate processing of other invoice events.")]
    protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
    {
        if (evt is not PayjoinInvoiceEvent invoiceEvent)
        {
            return;
        }

        _ = cancellationToken;
        for (var attempt = 1; attempt <= MaxWriteAttempts; attempt++)
        {
            GetShutdownDeadline().ThrowIfCancellationRequested();

            Exception writeFailure;
            try
            {
                await _writer.WriteAsync(invoiceEvent).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                writeFailure = ex;
            }

            var shutdownDeadline = GetShutdownDeadline();
            shutdownDeadline.ThrowIfCancellationRequested();
            if (attempt == MaxWriteAttempts)
            {
                LogWriteFailed(_logger, invoiceEvent.InvoiceId, writeFailure);
                return;
            }

            await Task.Delay(WriteRetryDelay, shutdownDeadline).ConfigureAwait(false);
        }
    }

    private CancellationToken GetShutdownDeadline()
    {
        lock (_stopLock)
        {
            return _shutdownDeadline;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Task stoppingTask;
        lock (_stopLock)
        {
            if (_stoppingTask is null)
            {
                _shutdownDeadline = cancellationToken;
                _stoppingTask = base.StopAsync(cancellationToken);
            }
            stoppingTask = _stoppingTask;
        }

        try
        {
            await stoppingTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogShutdownDeadlineReached(_logger, null);
            _ = ObserveStopCompletionAsync(stoppingTask);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Observe late completion after the shutdown deadline without an unobserved exception.")]
    private async Task ObserveStopCompletionAsync(Task stoppingTask)
    {
        try
        {
            await stoppingTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogShutdownFailed(_logger, ex);
        }
    }
}
