using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

/// <summary>
/// Ticks the sender session processor every five seconds, matching the receiver poller's cadence.
/// Ticks do not overlap: a slow tick delays the next one instead of running beside it.
/// </summary>
internal sealed class PayjoinSenderPoller : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogSenderTickFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(1, nameof(LogSenderTickFailed)),
            "Payjoin sender poll tick failed");

    private readonly IPayjoinSenderSessionProcessor _sessionProcessor;
    private readonly ILogger<PayjoinSenderPoller> _logger;

    internal PayjoinSenderPoller(
        IPayjoinSenderSessionProcessor sessionProcessor,
        ILogger<PayjoinSenderPoller> logger)
    {
        _sessionProcessor = sessionProcessor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await _sessionProcessor.ProcessTickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogSenderTickFailed(_logger, ex);
            }
        }
    }
}
