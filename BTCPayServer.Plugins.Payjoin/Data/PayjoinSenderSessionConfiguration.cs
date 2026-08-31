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
        // The status is the row's optimistic concurrency guard: every transition writes it, so
        // a save carries "where status is still what I read" and a late writer loses instead of
        // overwriting a terminal state. This is model metadata only; the schema is unchanged.
        entity.Property(x => x.Status).IsConcurrencyToken();
        // One live session per URI per store, enforced where the in-process build lock cannot
        // reach: across processes and across restarts. The filter is raw SQL, so the statuses
        // are spelled from the enum rather than as magic values.
        entity.HasIndex(x => new { x.StoreId, x.Bip21 })
            .IsUnique()
            .HasFilter($"\"Status\" IN ({(int)PayjoinSenderSessionStatus.Pending}, {(int)PayjoinSenderSessionStatus.AwaitingSignature})")
            .HasDatabaseName(PayjoinPluginDbSchema.SenderSessionsLiveBip21Index);
        entity.Property(x => x.OutpointsUsed).HasColumnType("text[]");
        entity.HasMany(x => x.Events)
            .WithOne(x => x.Session)
            .HasForeignKey(x => x.SenderSessionId)
            .HasConstraintName(PayjoinPluginDbSchema.SenderSessionEventsSessionForeignKey)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
