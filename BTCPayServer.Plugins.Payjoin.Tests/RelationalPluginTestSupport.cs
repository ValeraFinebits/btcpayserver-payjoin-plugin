using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using System.Diagnostics.CodeAnalysis;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BTCPayServer.Plugins.Payjoin.Tests;

/// <summary>
/// Shared SQLite-backed fixture for tests that drive the real store and bridge services across a
/// relational database, including fault injection for persistence-failure flows.
/// </summary>
internal sealed class RelationalPluginTestContext : IDisposable
{
    private readonly SqliteTestPayjoinPluginDbContextFactory _dbContextFactory = new();
    private readonly SqliteUniqueConstraintViolationDetector _uniqueConstraintViolationDetector = new();

    public PayjoinReceiverSessionStore CreateStore() => new(_dbContextFactory, _uniqueConstraintViolationDetector);

    public PayjoinReceiverSessionStore CreateStore(ILogger<PayjoinReceiverSessionStore> logger) =>
        new(_dbContextFactory, _uniqueConstraintViolationDetector, logger: logger);

    public PayjoinSeenInputStore CreateSeenInputStore() => new(_dbContextFactory);

    public PayjoinSessionBuildLock SessionBuildLock { get; } = new();

    public PayjoinAccountingBridgeService CreateBridgeService() => new(_dbContextFactory, SessionBuildLock);

    public PayjoinAttentionRecordSeeder CreateAttentionRecordSeeder() => new(_dbContextFactory, _uniqueConstraintViolationDetector, SessionBuildLock);

    public Task<PayjoinAccountingBridgeState> SeedBridgeAsync(CreatePayjoinAccountingBridgeRequest request, CancellationToken token) => _dbContextFactory.SeedBridgeAsync(request, token);

    public PayjoinPluginDbContext CreateDbContext() => _dbContextFactory.CreateContext();

    /// <summary>
    /// While set, every SaveChanges on newly created contexts throws, so tests can observe what a
    /// flow leaves behind when its persistence step fails.
    /// </summary>
    public bool FailSaveChanges
    {
        get => _dbContextFactory.FailSaveChanges;
        set => _dbContextFactory.FailSaveChanges = value;
    }

    public Action? BeforeNextSaveChanges
    {
        get => _dbContextFactory.BeforeNextSaveChanges;
        set => _dbContextFactory.BeforeNextSaveChanges = value;
    }

    public void BreakDatabase() => _dbContextFactory.Dispose();

    public void Dispose()
    {
        using var context = _dbContextFactory.CreateContext();
        context.Database.EnsureDeleted();
        _dbContextFactory.Dispose();
    }
}

internal sealed class SqliteUniqueConstraintViolationDetector : IPayjoinUniqueConstraintViolationDetector
{
    public bool IsUniqueConstraintViolation(DbUpdateException exception, string constraintName)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(constraintName);

        if (exception.InnerException is not SqliteException sqliteException)
        {
            return false;
        }

        return sqliteException.SqliteErrorCode == 19 &&
               (sqliteException.SqliteExtendedErrorCode == 19 ||
                sqliteException.SqliteExtendedErrorCode == 1555 ||
                sqliteException.SqliteExtendedErrorCode == 2067);
    }
}

internal sealed class SqliteTestPayjoinPluginDbContextFactory : PayjoinPluginDbContextFactory, IDisposable
{
    private readonly string _connectionString;
    private readonly SqliteConnection _keeperConnection;

    public SqliteTestPayjoinPluginDbContextFactory()
        : base(Options.Create(new DatabaseOptions
        {
            ConnectionString = "Data Source=:memory:"
        }))
    {
        _connectionString = $"Data Source=payjoin-relational-tests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keeperConnection = new SqliteConnection(_connectionString);
        _keeperConnection.Open();

        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    public bool FailSaveChanges { get; set; }

    public Action<string>? OnReaderExecuting { get; set; }

    public Action? BeforeNextSaveChanges { get; set; }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The created SQLite connection is owned and disposed by SqliteOwnedPayjoinPluginDbContext.")]
    public override PayjoinPluginDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptionsAction = null)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            var dbContextOptions = new DbContextOptionsBuilder<PayjoinPluginDbContext>()
                .UseSqlite(connection, sqliteOptions => sqliteOptions.CommandTimeout(30))
                .AddInterceptors(new RecordingQueryInterceptor(this))
                .Options;

            return new SqliteOwnedPayjoinPluginDbContext(dbContextOptions, connection, this);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _keeperConnection.Dispose();
    }

    private sealed class RecordingQueryInterceptor(SqliteTestPayjoinPluginDbContextFactory factory) : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            factory.OnReaderExecuting?.Invoke(command.CommandText);
            return result;
        }
    }

    private sealed class SqliteOwnedPayjoinPluginDbContext : PayjoinPluginDbContext
    {
        private readonly SqliteConnection _connection;
        private readonly SqliteTestPayjoinPluginDbContextFactory _factory;

        public SqliteOwnedPayjoinPluginDbContext(
            DbContextOptions<PayjoinPluginDbContext> options,
            SqliteConnection connection,
            SqliteTestPayjoinPluginDbContextFactory factory)
            : base(options)
        {
            _connection = connection;
            _factory = factory;
        }

        protected override void OnModelCreating(Microsoft.EntityFrameworkCore.ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // SQLite cannot order or compare DateTimeOffset columns, so the tests store them as
            // encoded ticks. Production runs on PostgreSQL, which supports the type natively.
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var property in entityType.GetProperties())
                {
                    if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                    {
                        property.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter());
                    }
                }
            }
        }

        public override int SaveChanges(bool acceptAllChangesOnSuccess)
        {
            PrepareSaveChanges();
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }

        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            PrepareSaveChanges();
            return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        private void PrepareSaveChanges()
        {
            var beforeSaveChanges = _factory.BeforeNextSaveChanges;
            _factory.BeforeNextSaveChanges = null;
            beforeSaveChanges?.Invoke();

            if (_factory.FailSaveChanges)
            {
                throw new DbUpdateException("Injected persistence failure.");
            }
        }

        public override void Dispose()
        {
            base.Dispose();
            _connection.Dispose();
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync().ConfigureAwait(false);
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}

internal static class AccountingBridgeTestData
{
    internal static void SeedBridgeFields(this PayjoinPluginDbContextFactory factory, string invoiceId,
        Action<BTCPayServer.Plugins.Payjoin.Data.PayjoinAccountingBridgeData> seed)
    {
        using var context = factory.CreateContext();
        seed(context.AccountingBridges.Single(x => x.InvoiceId == invoiceId));
        var session = context.ReceiverSessions.SingleOrDefault(x => x.InvoiceId == invoiceId);
        if (session is not null) session.AttemptVersion = checked(session.AttemptVersion + 1);
        context.SaveChanges();
    }

    internal static PayjoinReceiverSessionStore.DatabaseBackedReceiverPersister Owner(this PayjoinReceiverSessionStore store, string invoiceId)
    {
        if (!store.TryGetSession(invoiceId, out var session)) throw new InvalidOperationException("Missing fixture attempt.");
        return store.CreatePersister(session!);
    }

    internal static async Task<PayjoinAccountingBridgeState> SeedBridgeAsync(this PayjoinPluginDbContextFactory factory,
        CreatePayjoinAccountingBridgeRequest request, CancellationToken token)
    {
        using var context = factory.CreateContext();
        var now = DateTimeOffset.UtcNow;
        context.AccountingBridges.Add(new BTCPayServer.Plugins.Payjoin.Data.PayjoinAccountingBridgeData
        {
            InvoiceId = request.InvoiceId,
            StoreId = request.StoreId,
            CryptoCode = request.CryptoCode,
            PaymentMethodId = request.PaymentMethodId,
            ExpiresAt = request.ExpiresAt,
            FallbackTransactionId = request.FallbackTransactionId,
            FallbackOutputIndex = request.FallbackOutputIndex,
            FallbackValueSats = request.FallbackValueSats,
            EffectiveInvoiceValueSats = request.EffectiveInvoiceValueSats,
            SettlementScript = request.SettlementScript,
            SettlementKeyPath = request.SettlementKeyPath,
            ExpectedFinalTransactionId = request.ExpectedFinalTransactionId,
            ExpectedFinalOutputIndex = request.ExpectedFinalOutputIndex,
            ExpectedFinalValueSats = request.ExpectedFinalValueSats,
            Status = request.ExpectedFinalTransactionId is null
                ? BTCPayServer.Plugins.Payjoin.Data.PayjoinAccountingBridgeStatus.PendingFallback
                : BTCPayServer.Plugins.Payjoin.Data.PayjoinAccountingBridgeStatus.PendingFinalTransaction,
            CreatedAt = now,
            UpdatedAt = now
        });
        await context.SaveChangesAsync(token).ConfigureAwait(false);
        return (await new PayjoinAccountingBridgeService(factory, new PayjoinSessionBuildLock()).TryGetByInvoiceIdAsync(request.InvoiceId, token).ConfigureAwait(false))!;
    }
}
