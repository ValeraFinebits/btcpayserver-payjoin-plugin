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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        System.ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema(PayjoinPluginDbSchema.SchemaName);
        modelBuilder.Entity<PluginData>()
            .ToTable(PayjoinPluginDbSchema.PluginRecordsTable);
        modelBuilder.Entity<PayjoinReceiverSessionData>(entity =>
        {
            entity.ToTable(PayjoinPluginDbSchema.ReceiverSessionsTable);
            entity.HasKey(x => x.InvoiceId);
            entity.Property(x => x.ReceiverAddress).HasMaxLength(PayjoinPluginDbSchema.ReceiverAddressMaxLength);
            entity.Property(x => x.OhttpRelayUrl).HasMaxLength(PayjoinPluginDbSchema.OhttpRelayUrlMaxLength);
            entity.Property(x => x.ContributedInputTransactionId).HasMaxLength(PayjoinPluginDbSchema.TransactionIdMaxLength);
            entity.HasMany(x => x.Events)
                .WithOne(x => x.Session)
                .HasForeignKey(x => x.InvoiceId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<PayjoinReceiverSessionEventData>(entity =>
        {
            entity.ToTable(PayjoinPluginDbSchema.ReceiverSessionEventsTable);
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.InvoiceId, x.Sequence })
                .IsUnique()
                .HasDatabaseName(PayjoinPluginDbSchema.ReceiverSessionEventsInvoiceSequenceIndex);
        });
    }
}
