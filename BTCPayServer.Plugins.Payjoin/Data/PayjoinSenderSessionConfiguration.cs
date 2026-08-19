using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BTCPayServer.Plugins.Payjoin.Data;

internal sealed class PayjoinSenderSessionConfiguration : IEntityTypeConfiguration<PayjoinSenderSessionData>
{
    public void Configure(EntityTypeBuilder<PayjoinSenderSessionData> entity)
    {
        entity.ToTable(PayjoinPluginDbSchema.SenderSessionsTable);
        entity.HasKey(x => x.SenderSessionId)
            .HasName(PayjoinPluginDbSchema.SenderSessionsPrimaryKey);
        entity.Property(x => x.SenderSessionId).HasMaxLength(PayjoinPluginDbSchema.SenderSessionIdMaxLength);
        entity.Property(x => x.DestinationAddress).HasMaxLength(PayjoinPluginDbSchema.ReceiverAddressMaxLength);
        entity.Property(x => x.OriginalTransactionId).HasMaxLength(PayjoinPluginDbSchema.TransactionIdMaxLength);
        entity.Property(x => x.BroadcastTransactionId).HasMaxLength(PayjoinPluginDbSchema.TransactionIdMaxLength);
        entity.Property(x => x.FailureMessage).HasMaxLength(PayjoinPluginDbSchema.BridgeFailureMessageMaxLength);
        entity.HasIndex(x => x.StoreId)
            .HasDatabaseName(PayjoinPluginDbSchema.SenderSessionsStoreIdIndex);
        entity.HasIndex(x => new { x.Status, x.CreatedAt })
            .HasDatabaseName(PayjoinPluginDbSchema.SenderSessionsStatusCreatedAtIndex);
        entity.HasIndex(x => x.OriginalTransactionId)
            .HasDatabaseName(PayjoinPluginDbSchema.SenderSessionsOriginalTransactionIdIndex);
        entity.Property(x => x.PendingTransactionId).HasMaxLength(PayjoinPluginDbSchema.SenderSessionIdMaxLength);
        entity.HasIndex(x => x.PendingTransactionId)
            .HasDatabaseName(PayjoinPluginDbSchema.SenderSessionsPendingTransactionIdIndex);
        entity.Property(x => x.CoinReservationTransactionId).HasMaxLength(PayjoinPluginDbSchema.SenderSessionIdMaxLength);
        // One live session per URI, enforced where the in-process build lock cannot reach:
        // across processes and across restarts. The filter names the statuses by value because
        // the filter is raw SQL: 0 is Pending and 4 is AwaitingSignature.
        entity.HasIndex(x => x.Bip21)
            .IsUnique()
            .HasFilter("\"Status\" IN (0, 4)")
            .HasDatabaseName(PayjoinPluginDbSchema.SenderSessionsLiveBip21Index);
        entity.Property(x => x.OutpointsUsed).HasColumnType("text[]");
        entity.HasMany(x => x.Events)
            .WithOne(x => x.Session)
            .HasForeignKey(x => x.SenderSessionId)
            .HasConstraintName(PayjoinPluginDbSchema.SenderSessionEventsSessionForeignKey)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
