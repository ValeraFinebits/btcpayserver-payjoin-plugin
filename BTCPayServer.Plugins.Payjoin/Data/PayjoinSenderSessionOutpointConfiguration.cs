using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BTCPayServer.Plugins.Payjoin.Data;

internal sealed class PayjoinSenderSessionOutpointConfiguration : IEntityTypeConfiguration<PayjoinSenderSessionOutpointData>
{
    public void Configure(EntityTypeBuilder<PayjoinSenderSessionOutpointData> entity)
    {
        entity.ToTable(PayjoinPluginDbSchema.SenderSessionOutpointsTable);
        entity.HasKey(x => x.Outpoint)
            .HasName(PayjoinPluginDbSchema.SenderSessionOutpointsPrimaryKey);
        entity.Property(x => x.Outpoint).HasMaxLength(PayjoinPluginDbSchema.OutpointMaxLength);
        entity.Property(x => x.SenderSessionId).HasMaxLength(PayjoinPluginDbSchema.SenderSessionIdMaxLength);
        entity.HasIndex(x => x.SenderSessionId)
            .HasDatabaseName(PayjoinPluginDbSchema.SenderSessionOutpointsSessionIndex);
        entity.HasOne(x => x.Session)
            .WithMany()
            .HasForeignKey(x => x.SenderSessionId)
            .HasConstraintName(PayjoinPluginDbSchema.SenderSessionOutpointsSessionForeignKey)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
