using BTCPayServer.Plugins.Payjoin.Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

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
        var context = _pluginDbContextFactory.CreateContext();
        try
        {
            await context.PluginRecords.AddAsync(new PluginData { Timestamp = DateTimeOffset.UtcNow }).ConfigureAwait(false);
            await context.SaveChangesAsync().ConfigureAwait(false);
        }
        finally
        {
            await context.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<List<PluginData>> Get()
    {
        var context = _pluginDbContextFactory.CreateContext();
        try
        {
            return await context.PluginRecords.ToListAsync().ConfigureAwait(false);
        }
        finally
        {
            await context.DisposeAsync().ConfigureAwait(false);
        }
    }
}

