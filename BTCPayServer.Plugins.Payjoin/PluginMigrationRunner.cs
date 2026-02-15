using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin;

public class PluginMigrationRunner : IHostedService
{
    private readonly PayjoinPluginDbContextFactory _pluginDbContextFactory;
    private readonly PayjoinPluginService _pluginService;
    private readonly ISettingsRepository _settingsRepository;

    public PluginMigrationRunner(
        ISettingsRepository settingsRepository,
        PayjoinPluginDbContextFactory pluginDbContextFactory,
        PayjoinPluginService pluginService)
    {
        _settingsRepository = settingsRepository;
        _pluginDbContextFactory = pluginDbContextFactory;
        _pluginService = pluginService;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = await _settingsRepository.GetSettingAsync<PluginDataMigrationHistory>().ConfigureAwait(false) ??
                       new PluginDataMigrationHistory();
        var ctx = _pluginDbContextFactory.CreateContext();
        try
        {
            await ctx.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await ctx.DisposeAsync().ConfigureAwait(false);
        }

        // settings migrations
        if (!settings.UpdatedSomething)
        {
            settings.UpdatedSomething = true;
            await _settingsRepository.UpdateSetting(settings).ConfigureAwait(false);
        }

        // test record
        await _pluginService.AddTestDataRecord().ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private class PluginDataMigrationHistory
    {
        public bool UpdatedSomething { get; set; }
    }
}

