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
    private readonly PayjoinReceiverSessionStore _sessionStore;
    private readonly IPayjoinReceiverSessionProcessor _sessionProcessor;
    private readonly ILogger<PayjoinReceiverPoller> _logger;

    public PayjoinReceiverPoller(
        PayjoinReceiverSessionStore sessionStore,
        IPayjoinReceiverSessionProcessor sessionProcessor,
        ILogger<PayjoinReceiverPoller> logger)
    {
        _sessionStore = sessionStore;
        _sessionProcessor = sessionProcessor;
        _logger = logger;
    }

    internal Task ProcessTickOnceAsync(CancellationToken stoppingToken)
    {
        _sessionStore.CleanupExpiredInputReservations(DateTimeOffset.UtcNow);
        return _sessionProcessor.ProcessTickAsync(stoppingToken);
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
