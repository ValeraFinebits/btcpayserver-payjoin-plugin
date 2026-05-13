using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BTCPayServer.Plugins.Payjoin.Data;

internal sealed class PayjoinReceiverSessionEventConfiguration : IEntityTypeConfiguration<PayjoinReceiverSessionEventData>
{
    public void Configure(EntityTypeBuilder<PayjoinReceiverSessionEventData> entity)
    {
        entity.ToTable(PayjoinPluginDbSchema.ReceiverSessionEventsTable);
        entity.HasKey(x => x.Id)
            .HasName(PayjoinPluginDbSchema.ReceiverSessionEventsPrimaryKey);
        entity.HasIndex(x => new { x.InvoiceId, x.Sequence })
            .IsUnique()
            .HasDatabaseName(PayjoinPluginDbSchema.ReceiverSessionEventsInvoiceSequenceIndex);
    }
}
