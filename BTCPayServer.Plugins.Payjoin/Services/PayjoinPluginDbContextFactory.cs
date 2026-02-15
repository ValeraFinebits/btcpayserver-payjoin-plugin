using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using System;

namespace BTCPayServer.Plugins.Payjoin.Services;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PayjoinPluginDbContext>
{
    public PayjoinPluginDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<PayjoinPluginDbContext>();

        // FIXME: Somehow the DateTimeOffset column types get messed up when not using Postgres
        // https://docs.microsoft.com/en-us/ef/core/managing-schemas/migrations/providers?tabs=dotnet-core-cli
        builder.UseNpgsql("User ID=postgres;Host=127.0.0.1;Port=39372;Database=designtimebtcpay");

        return new PayjoinPluginDbContext(builder.Options, true);
    }
}

public class PayjoinPluginDbContextFactory : BaseDbContextFactory<PayjoinPluginDbContext>
{
    public PayjoinPluginDbContextFactory(IOptions<DatabaseOptions> options) : base(options, "BTCPayServer.Plugins.Payjoin")
    {
    }

    public override PayjoinPluginDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder> npgsqlOptionsAction = null)
    {
        var builder = new DbContextOptionsBuilder<PayjoinPluginDbContext>();
        ConfigureBuilder(builder, npgsqlOptionsAction);
        return new PayjoinPluginDbContext(builder.Options);
    }
}
