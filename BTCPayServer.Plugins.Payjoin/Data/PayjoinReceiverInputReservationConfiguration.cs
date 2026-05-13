using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BTCPayServer.Plugins.Payjoin.Data;

internal sealed class PayjoinReceiverInputReservationConfiguration : IEntityTypeConfiguration<PayjoinReceiverInputReservationData>
{
    public void Configure(EntityTypeBuilder<PayjoinReceiverInputReservationData> entity)
    {
        entity.ToTable(PayjoinPluginDbSchema.ReceiverInputReservationsTable);
        entity.HasKey(x => x.Id)
            .HasName(PayjoinPluginDbSchema.ReceiverInputReservationsPrimaryKey);
        entity.Property(x => x.TransactionId).HasMaxLength(PayjoinPluginDbSchema.TransactionIdMaxLength);
        entity.HasIndex(x => new { x.TransactionId, x.OutputIndex })
            .IsUnique()
            .HasDatabaseName(PayjoinPluginDbSchema.ReceiverInputReservationsOutPointIndex);
        entity.HasIndex(x => x.InvoiceId)
            .HasDatabaseName(PayjoinPluginDbSchema.ReceiverInputReservationsInvoiceIdIndex);
        entity.HasIndex(x => x.StoreId)
            .HasDatabaseName(PayjoinPluginDbSchema.ReceiverInputReservationsStoreIdIndex);
        entity.HasIndex(x => x.ExpiresAt)
            .HasDatabaseName(PayjoinPluginDbSchema.ReceiverInputReservationsExpiresAtIndex);
    }
}
