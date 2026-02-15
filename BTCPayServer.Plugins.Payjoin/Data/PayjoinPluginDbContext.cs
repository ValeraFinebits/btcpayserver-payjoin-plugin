using BTCPayServer.Plugins.Payjoin.Data;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Payjoin;

public class PayjoinPluginDbContext : DbContext
{
    private readonly bool _designTime;

    public PayjoinPluginDbContext(DbContextOptions<PayjoinPluginDbContext> options, bool designTime = false)
        : base(options)
    {
        _designTime = designTime;
    }

    public DbSet<PluginData> PluginRecords { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("BTCPayServer.Plugins.Payjoin");
    }
}
