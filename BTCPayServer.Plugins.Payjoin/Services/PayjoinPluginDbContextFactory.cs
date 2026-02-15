using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.Template.Services;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PayjoinPluginDbContext>
{
    public PayjoinPluginDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<PayjoinPluginDbContext>();

        // FIXME: Somehow the DateTimeOffset column types get messed up when not using Postgres
        // https://docs.microsoft.com/en-us/ef/core/managing-schemas/migrations/providers?tabs=dotnet-core-cli
        builder.UseNpgsql("User ID=postgres;Host=127.0.0.1;Port=39372;Database=designtimebtcpay");

        return new MyPluginDbContext(builder.Options, true);
    }
}

public class PayjoinPluginDbContextFactory : BaseDbContextFactory<PayjoinPluginDbContext>
{
    public PayjoinPluginDbContextFactory(IOptions<DatabaseOptions> options) : base(options, "BTCPayServer.Plugins.Template")
    {
    }

    public override PayjoinPluginDbContext CreateContext()
    {
        var builder = new DbContextOptionsBuilder<PayjoinPluginDbContext>();
        ConfigureBuilder(builder);
        return new MyPluginDbContext(builder.Options);
    }
}
