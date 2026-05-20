using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BTCPayServer.Plugins.Payjoin.Migrations;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PayjoinPluginDbContext>
{
    public PayjoinPluginDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<PayjoinPluginDbContext>();

        // FIXME: Somehow the DateTimeOffset column types get messed up when not using Postgres
        // https://docs.microsoft.com/en-us/ef/core/managing-schemas/migrations/providers?tabs=dotnet-core-cli
        builder.UseNpgsql(
            "User ID=postgres;Host=127.0.0.1;Port=39372;Database=btcpayserver",
            o => o.MigrationsHistoryTable("BTCPayServer.Plugins.Payjoin"));

        return new PayjoinPluginDbContext(builder.Options, designTime: true);
    }
}
