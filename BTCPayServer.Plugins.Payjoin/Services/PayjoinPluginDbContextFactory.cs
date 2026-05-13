using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using System;

namespace BTCPayServer.Plugins.Payjoin.Services;

public class PayjoinPluginDbContextFactory : BaseDbContextFactory<PayjoinPluginDbContext>
{
    public PayjoinPluginDbContextFactory(IOptions<DatabaseOptions> options) : base(options, "BTCPayServer.Plugins.Payjoin")
    {
    }

    public override PayjoinPluginDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptionsAction = null)
    {
        var builder = new DbContextOptionsBuilder<PayjoinPluginDbContext>();
        ConfigureBuilder(builder, npgsqlOptionsAction);
        return new PayjoinPluginDbContext(builder.Options);
    }
}
