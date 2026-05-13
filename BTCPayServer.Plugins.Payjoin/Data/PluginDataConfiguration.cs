using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BTCPayServer.Plugins.Payjoin.Data;

internal sealed class PluginDataConfiguration : IEntityTypeConfiguration<PluginData>
{
    public void Configure(EntityTypeBuilder<PluginData> entity)
    {
        entity.ToTable(PayjoinPluginDbSchema.PluginRecordsTable);
        entity.HasKey(x => x.Id)
            .HasName(PayjoinPluginDbSchema.PluginRecordsPrimaryKey);
    }
}
