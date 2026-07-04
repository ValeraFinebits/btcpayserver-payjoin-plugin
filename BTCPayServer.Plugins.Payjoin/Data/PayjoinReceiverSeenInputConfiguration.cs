using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BTCPayServer.Plugins.Payjoin.Data;

internal sealed class PayjoinReceiverSeenInputConfiguration : IEntityTypeConfiguration<PayjoinReceiverSeenInputData>
{
    public void Configure(EntityTypeBuilder<PayjoinReceiverSeenInputData> entity)
    {
        entity.ToTable(PayjoinPluginDbSchema.ReceiverSeenInputsTable);
        entity.HasKey(x => x.Id)
            .HasName(PayjoinPluginDbSchema.ReceiverSeenInputsPrimaryKey);
        entity.Property(x => x.TransactionId).HasMaxLength(PayjoinPluginDbSchema.TransactionIdMaxLength);
        entity.HasIndex(x => new { x.TransactionId, x.OutputIndex })
            .IsUnique()
            .HasDatabaseName(PayjoinPluginDbSchema.ReceiverSeenInputsOutPointIndex);
    }
}
