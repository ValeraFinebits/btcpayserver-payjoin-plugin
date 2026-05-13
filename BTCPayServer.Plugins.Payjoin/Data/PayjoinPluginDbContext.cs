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

    public DbSet<PluginData> PluginRecords { get; set; } = null!;

    internal DbSet<PayjoinReceiverSessionData> ReceiverSessions { get; set; } = null!;

    internal DbSet<PayjoinReceiverSessionEventData> ReceiverSessionEvents { get; set; } = null!;

    internal DbSet<PayjoinReceiverInputReservationData> ReceiverInputReservations { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        System.ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema(PayjoinPluginDbSchema.SchemaName);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PayjoinPluginDbContext).Assembly);
    }
}
