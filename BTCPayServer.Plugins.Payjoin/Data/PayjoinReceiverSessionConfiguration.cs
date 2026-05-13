using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BTCPayServer.Plugins.Payjoin.Data;

internal sealed class PayjoinReceiverSessionConfiguration : IEntityTypeConfiguration<PayjoinReceiverSessionData>
{
    public void Configure(EntityTypeBuilder<PayjoinReceiverSessionData> entity)
    {
        entity.ToTable(PayjoinPluginDbSchema.ReceiverSessionsTable);
        entity.HasKey(x => x.InvoiceId)
            .HasName(PayjoinPluginDbSchema.ReceiverSessionsPrimaryKey);
        entity.Property(x => x.ReceiverAddress).HasMaxLength(PayjoinPluginDbSchema.ReceiverAddressMaxLength);
        entity.Property(x => x.OhttpRelayUrl).HasMaxLength(PayjoinPluginDbSchema.OhttpRelayUrlMaxLength);
        entity.Property(x => x.ContributedInputTransactionId).HasMaxLength(PayjoinPluginDbSchema.TransactionIdMaxLength);
        entity.HasMany(x => x.Events)
            .WithOne(x => x.Session)
            .HasForeignKey(x => x.InvoiceId)
            .HasConstraintName(PayjoinPluginDbSchema.ReceiverSessionEventsSessionForeignKey)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasMany(x => x.InputReservations)
            .WithOne(x => x.Session)
            .HasForeignKey(x => x.InvoiceId)
            .HasConstraintName(PayjoinPluginDbSchema.ReceiverInputReservationsSessionForeignKey)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
