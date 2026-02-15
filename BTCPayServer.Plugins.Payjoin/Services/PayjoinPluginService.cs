using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Payjoin.Data;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Payjoin.Services;

public class PayjoinPluginService
{
    private readonly PayjoinPluginDbContextFactory _pluginDbContextFactory;

    public PayjoinPluginService(PayjoinPluginDbContextFactory pluginDbContextFactory)
    {
        _pluginDbContextFactory = pluginDbContextFactory;
    }

    public async Task AddTestDataRecord()
    {
        await using var context = _pluginDbContextFactory.CreateContext();

        await context.PluginRecords.AddAsync(new PluginData { Timestamp = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();
    }

    public async Task<List<PluginData>> Get()
    {
        await using var context = _pluginDbContextFactory.CreateContext();

        return await context.PluginRecords.ToListAsync();
    }
}

