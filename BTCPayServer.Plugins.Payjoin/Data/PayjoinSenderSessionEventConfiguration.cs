using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BTCPayServer.Plugins.Payjoin.Data;

internal sealed class PayjoinSenderSessionEventConfiguration : IEntityTypeConfiguration<PayjoinSenderSessionEventData>
{
    public void Configure(EntityTypeBuilder<PayjoinSenderSessionEventData> entity)
    {
        entity.ToTable(PayjoinPluginDbSchema.SenderSessionEventsTable);
        entity.HasKey(x => x.Id)
            .HasName(PayjoinPluginDbSchema.SenderSessionEventsPrimaryKey);
        entity.HasIndex(x => new { x.SenderSessionId, x.Sequence })
            .IsUnique()
            .HasDatabaseName(PayjoinPluginDbSchema.SenderSessionEventsSessionSequenceIndex);
    }
}
