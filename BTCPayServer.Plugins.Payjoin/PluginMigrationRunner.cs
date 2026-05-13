using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin;

public class PluginMigrationRunner : IHostedService
{
    private static readonly Action<ILogger, Exception?> LogPluginMigrationCancelled =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(1, nameof(LogPluginMigrationCancelled)),
            "Payjoin plugin startup migration was cancelled.");
    private static readonly Action<ILogger, Exception?> LogPluginMigrationFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(2, nameof(LogPluginMigrationFailed)),
            "Payjoin plugin startup migration failed. The server will continue running, but plugin startup work may be incomplete.");

    private readonly PayjoinPluginDbContextFactory _pluginDbContextFactory;
    private readonly ILogger<PluginMigrationRunner> _logger;

    public PluginMigrationRunner(
        PayjoinPluginDbContextFactory pluginDbContextFactory,
        ILogger<PluginMigrationRunner> logger)
    {
        _pluginDbContextFactory = pluginDbContextFactory;
        _logger = logger;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Plugin startup is an isolation boundary and must not crash the host process.")]
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var ctx = _pluginDbContextFactory.CreateContext();
            try
            {
                await MigrateAsync(ctx, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await ctx.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogPluginMigrationCancelled(_logger, null);
        }
        catch (Exception ex)
        {
            // TODO: Revisit whether migration failures should remain fail-soft or become fail-fast once operational guarantees are defined.
            LogPluginMigrationFailed(_logger, ex);
        }
    }

    protected internal virtual Task MigrateAsync(PayjoinPluginDbContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Database.MigrateAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

