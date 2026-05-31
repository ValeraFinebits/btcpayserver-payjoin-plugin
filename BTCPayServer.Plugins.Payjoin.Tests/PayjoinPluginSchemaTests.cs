using BTCPayServer.Plugins.Payjoin.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class PayjoinPluginSchemaTests
{
    // TODO: Add a Postgres-backed smoke test that applies the real migrations to a clean database.
    [Fact]
    public void CurrentModelMatchesMigrationSnapshot()
    {
        using var context = CreateDesignTimeContext();
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public void CurrentModelMatchesSchemaConstants()
    {
        using var context = CreateDesignTimeContext();
        var model = context.Model;
        var receiverSessions = AssertEntity<PayjoinReceiverSessionData>(model, PayjoinPluginDbSchema.ReceiverSessionsTable);

        Assert.Equal(PayjoinPluginDbSchema.SchemaName, model.GetDefaultSchema());

        AssertKey(receiverSessions, PayjoinPluginDbSchema.ReceiverSessionsPrimaryKey);
        Assert.Equal(PayjoinPluginDbSchema.ReceiverAddressMaxLength, receiverSessions.FindProperty(nameof(PayjoinReceiverSessionData.ReceiverAddress))?.GetMaxLength());
        Assert.Equal(PayjoinPluginDbSchema.OhttpRelayUrlMaxLength, receiverSessions.FindProperty(nameof(PayjoinReceiverSessionData.OhttpRelayUrl))?.GetMaxLength());
        Assert.Equal(PayjoinPluginDbSchema.TransactionIdMaxLength, receiverSessions.FindProperty(nameof(PayjoinReceiverSessionData.ContributedInputTransactionId))?.GetMaxLength());

        var receiverSessionEvents = AssertEntity<PayjoinReceiverSessionEventData>(model, PayjoinPluginDbSchema.ReceiverSessionEventsTable);
        AssertKey(receiverSessionEvents, PayjoinPluginDbSchema.ReceiverSessionEventsPrimaryKey);
        AssertIndex(receiverSessionEvents, PayjoinPluginDbSchema.ReceiverSessionEventsInvoiceSequenceIndex, isUnique: true, nameof(PayjoinReceiverSessionEventData.InvoiceId), nameof(PayjoinReceiverSessionEventData.Sequence));
        AssertForeignKey(receiverSessionEvents, receiverSessions, PayjoinPluginDbSchema.ReceiverSessionEventsSessionForeignKey, DeleteBehavior.Cascade, nameof(PayjoinReceiverSessionEventData.InvoiceId));

        var receiverInputReservations = AssertEntity<PayjoinReceiverInputReservationData>(model, PayjoinPluginDbSchema.ReceiverInputReservationsTable);
        AssertKey(receiverInputReservations, PayjoinPluginDbSchema.ReceiverInputReservationsPrimaryKey);
        Assert.Equal(PayjoinPluginDbSchema.TransactionIdMaxLength, receiverInputReservations.FindProperty(nameof(PayjoinReceiverInputReservationData.TransactionId))?.GetMaxLength());
        AssertIndex(receiverInputReservations, PayjoinPluginDbSchema.ReceiverInputReservationsOutPointIndex, isUnique: true, nameof(PayjoinReceiverInputReservationData.TransactionId), nameof(PayjoinReceiverInputReservationData.OutputIndex));
        AssertIndex(receiverInputReservations, PayjoinPluginDbSchema.ReceiverInputReservationsInvoiceIdIndex, isUnique: false, nameof(PayjoinReceiverInputReservationData.InvoiceId));
        AssertIndex(receiverInputReservations, PayjoinPluginDbSchema.ReceiverInputReservationsStoreIdIndex, isUnique: false, nameof(PayjoinReceiverInputReservationData.StoreId));
        AssertIndex(receiverInputReservations, PayjoinPluginDbSchema.ReceiverInputReservationsExpiresAtIndex, isUnique: false, nameof(PayjoinReceiverInputReservationData.ExpiresAt));
        AssertForeignKey(receiverInputReservations, receiverSessions, PayjoinPluginDbSchema.ReceiverInputReservationsSessionForeignKey, DeleteBehavior.Cascade, nameof(PayjoinReceiverInputReservationData.InvoiceId));
    }

    private static PayjoinPluginDbContext CreateDesignTimeContext()
    {
        var options = new DbContextOptionsBuilder<PayjoinPluginDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=designtimebtcpay;Username=postgres")
            .Options;

        return new PayjoinPluginDbContext(options, designTime: true);
    }

    private static IEntityType AssertEntity<TEntity>(IModel model, string tableName)
    {
        var entityType = model.FindEntityType(typeof(TEntity));
        Assert.NotNull(entityType);
        Assert.Equal(tableName, entityType.GetTableName());
        Assert.Equal(PayjoinPluginDbSchema.SchemaName, entityType.GetSchema());
        return entityType;
    }

    private static void AssertKey(IEntityType entityType, string keyName)
    {
        Assert.Equal(keyName, entityType.FindPrimaryKey()?.GetName());
    }

    private static void AssertIndex(IEntityType entityType, string indexName, bool isUnique, params string[] propertyNames)
    {
        var index = entityType.GetIndexes().SingleOrDefault(candidate => string.Equals(candidate.GetDatabaseName(), indexName, StringComparison.Ordinal));
        Assert.NotNull(index);
        Assert.Equal(isUnique, index.IsUnique);
        Assert.Equal(propertyNames, index.Properties.Select(property => property.Name).ToArray());
    }

    private static void AssertForeignKey(IEntityType entityType, IEntityType principalEntityType, string foreignKeyName, DeleteBehavior deleteBehavior, params string[] propertyNames)
    {
        var foreignKey = entityType.GetForeignKeys().SingleOrDefault(candidate => string.Equals(candidate.GetConstraintName(), foreignKeyName, StringComparison.Ordinal));
        Assert.NotNull(foreignKey);
        Assert.Same(principalEntityType, foreignKey.PrincipalEntityType);
        Assert.Equal(deleteBehavior, foreignKey.DeleteBehavior);
        Assert.Equal(foreignKey.PrincipalEntityType.FindPrimaryKey()?.Properties.Select(property => property.Name).ToArray(), foreignKey.PrincipalKey.Properties.Select(property => property.Name).ToArray());
        Assert.Equal(propertyNames, foreignKey.Properties.Select(property => property.Name).ToArray());
    }
}
