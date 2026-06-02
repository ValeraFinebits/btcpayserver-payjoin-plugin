using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BTCPayServer.Plugins.Payjoin.Data;

internal sealed class PayjoinAccountingBridgeConfiguration : IEntityTypeConfiguration<PayjoinAccountingBridgeData>
{
    public void Configure(EntityTypeBuilder<PayjoinAccountingBridgeData> entity)
    {
        entity.ToTable(PayjoinPluginDbSchema.AccountingBridgesTable);
        entity.HasKey(x => x.Id)
            .HasName(PayjoinPluginDbSchema.AccountingBridgesPrimaryKey);
        entity.Property(x => x.CryptoCode)
            .HasMaxLength(PayjoinPluginDbSchema.CryptoCodeMaxLength);
        entity.Property(x => x.PaymentMethodId)
            .HasMaxLength(PayjoinPluginDbSchema.PaymentMethodIdMaxLength);
        entity.Property(x => x.FallbackTransactionId)
            .HasMaxLength(PayjoinPluginDbSchema.TransactionIdMaxLength);
        entity.Property(x => x.SettlementScript)
            .HasMaxLength(PayjoinPluginDbSchema.BridgeSettlementScriptMaxLength);
        entity.Property(x => x.ExpectedFinalTransactionId)
            .HasMaxLength(PayjoinPluginDbSchema.TransactionIdMaxLength);
        entity.Property(x => x.ExpectedFinalOutputIndex);
        entity.Property(x => x.FailureMessage)
            .HasMaxLength(PayjoinPluginDbSchema.BridgeFailureMessageMaxLength);
        entity.HasIndex(x => x.InvoiceId)
            .IsUnique()
            .HasDatabaseName(PayjoinPluginDbSchema.AccountingBridgesInvoiceIdIndex);
        entity.HasIndex(x => new { x.Status, x.CreatedAt })
            .HasDatabaseName(PayjoinPluginDbSchema.AccountingBridgesStatusCreatedAtIndex);
        entity.HasIndex(x => new { x.FallbackTransactionId, x.FallbackOutputIndex })
            .HasDatabaseName(PayjoinPluginDbSchema.AccountingBridgesFallbackOutPointIndex);
        entity.HasIndex(x => x.ExpectedFinalTransactionId)
            .HasDatabaseName(PayjoinPluginDbSchema.AccountingBridgesExpectedFinalTransactionIdIndex);
    }
}
