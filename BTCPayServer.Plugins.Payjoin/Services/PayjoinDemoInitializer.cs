using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Hosting;

namespace BTCPayServer.Plugins.Payjoin.Services;

public sealed class PayjoinDemoInitializer : IHostedService
{
    private const string PayjoinSettingsKey = "payjoin.settings";
    private readonly PayjoinDemoContext _context;
    private readonly StoreRepository _storeRepository;

    public PayjoinDemoInitializer(PayjoinDemoContext context, StoreRepository storeRepository)
    {
        _context = context;
        _storeRepository = storeRepository;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var stores = await _storeRepository.GetStores().ConfigureAwait(false);
        var demoStores = new List<(string StoreId, PayjoinStoreSettings Settings)>();
        foreach (var store in stores)
        {
            var settings = await _storeRepository.GetSettingAsync<PayjoinStoreSettings>(store.Id, PayjoinSettingsKey)
                .ConfigureAwait(false) ?? new PayjoinStoreSettings();
            if (!settings.DemoMode)
            {
                continue;
            }

            demoStores.Add((store.Id, settings));
        }

        if (demoStores.Count == 0)
        {
            return;
        }

        _context.Initialize();

        foreach (var demoStore in demoStores)
        {
            var settings = demoStore.Settings;

            if (_context.DirectoryUrl is not null)
            {
                settings.DirectoryUrl = _context.DirectoryUrl;
            }

            if (_context.OhttpRelayUrl is not null)
            {
                settings.OhttpRelayUrl = _context.OhttpRelayUrl;
            }

            await _storeRepository.UpdateSetting(demoStore.StoreId, PayjoinSettingsKey, settings).ConfigureAwait(false);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    
}
