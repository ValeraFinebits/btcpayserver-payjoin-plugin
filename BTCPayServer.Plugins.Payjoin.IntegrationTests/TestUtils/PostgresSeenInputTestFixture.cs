using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using NBitcoin;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using System.Data.Common;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;

internal sealed class PostgresSeenInputTestFixture : IAsyncDisposable
{
    private bool _ownsDatabase;

    public PostgresSeenInputTestFixture(DatabaseTester database, bool singleStatementBatches, string losingInvoice,
        OutPoint freshInput, OutPoint sharedInput, CancellationToken cancellationToken)
    {
        if (new NpgsqlConnectionStringBuilder(database.ConnectionString).Database != database.dbname)
        {
            throw new InvalidOperationException("DatabaseTester must provide a separate database for this fixture.");
        }
        Barrier = new SeenInputSaveBarrier(losingInvoice, freshInput, sharedInput, cancellationToken);
        Sql = new SeenInputPostgresObserver(Barrier, freshInput, sharedInput, singleStatementBatches);
        Factory = new InterceptingFactory(database.ConnectionString, singleStatementBatches, Barrier, Sql);
        Sessions = new PayjoinReceiverSessionStore(Factory, new PostgresPayjoinUniqueConstraintViolationDetector());
        SeenInputs = new PayjoinSeenInputStore(Factory);
    }

    public PayjoinPluginDbContextFactory Factory { get; }
    public PayjoinReceiverSessionStore Sessions { get; }
    public PayjoinSeenInputStore SeenInputs { get; }
    public SeenInputSaveBarrier Barrier { get; }
    public SeenInputPostgresObserver Sql { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        using var context = Factory.CreateContext();
        await context.GetService<IRelationalDatabaseCreator>().CreateAsync(cancellationToken).ConfigureAwait(false);
        _ownsDatabase = true;
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Barrier.Release();
        try
        {
            if (_ownsDatabase)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var context = Factory.CreateContext();
                await context.Database.EnsureDeletedAsync(cleanup.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            Barrier.Dispose();
        }
    }

    private sealed class InterceptingFactory(string connectionString, bool singleStatementBatches,
        params IInterceptor[] interceptors)
        : PayjoinPluginDbContextFactory(Options.Create(new DatabaseOptions { ConnectionString = connectionString }))
    {
        public override PayjoinPluginDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptionsAction = null)
        {
            var builder = new DbContextOptionsBuilder<PayjoinPluginDbContext>();
            ConfigureBuilder(builder, options =>
            {
                npgsqlOptionsAction?.Invoke(options);
                if (singleStatementBatches)
                {
                    options.MaxBatchSize(1);
                }
            });
            builder.AddInterceptors(interceptors);
            return new PayjoinPluginDbContext(builder.Options);
        }
    }
}

internal sealed class SeenInputSaveBarrier(string losingInvoice, OutPoint freshInput, OutPoint sharedInput,
    CancellationToken cancellationToken) : SaveChangesInterceptor, IDisposable
{
    private readonly ManualResetEventSlim _resume = new();
    private readonly HashSet<DbContextId> _savingContexts = [];
    private int _armed;

    public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public DbContextId? LosingContext { get; private set; }
    public (string TransactionId, long OutputIndex)[] StagedInputs { get; private set; } = [];
    public List<DbUpdateException> Failures { get; } = [];

    public void Arm() => Volatile.Write(ref _armed, 1);
    public void Release() => _resume.Set();
    public void Dispose() => _resume.Dispose();
    public bool IsLosingSave(DbContext? context) => context is not null && _savingContexts.Contains(context.ContextId);

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        var context = eventData.Context!;
        if (!context.ChangeTracker.Entries<PayjoinReceiverSessionEventData>()
            .Any(entry => entry.State == EntityState.Added && entry.Entity.InvoiceId == losingInvoice))
        {
            return result;
        }
        _savingContexts.Add(context.ContextId);
        if (Interlocked.CompareExchange(ref _armed, 0, 1) != 1)
        {
            return result;
        }

        StagedInputs = context.ChangeTracker.Entries<PayjoinReceiverSeenInputData>()
            .Where(entry => entry.State == EntityState.Added)
            .Select(entry => (entry.Entity.TransactionId, entry.Entity.OutputIndex)).ToArray();
        if (!StagedInputs.Contains((freshInput.Hash.ToString(), (long)freshInput.N))
            || !StagedInputs.Contains((sharedInput.Hash.ToString(), (long)sharedInput.N)))
        {
            throw new InvalidOperationException("The losing transition must stage both inspected inputs before saving.");
        }

        LosingContext = context.ContextId;
        Reached.SetResult();
        if (!_resume.Wait(TimeSpan.FromSeconds(30), cancellationToken))
        {
            throw new TimeoutException("The winning seen-input transition did not complete.");
        }
        return result;
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (IsLosingSave(eventData.Context) && eventData.Exception is DbUpdateException failure)
        {
            Failures.Add(failure);
        }
    }
}

internal sealed class SeenInputPostgresObserver(SeenInputSaveBarrier barrier, OutPoint freshInput, OutPoint sharedInput,
    bool observePartialInsert) : DbCommandInterceptor
{
    public bool ConflictHadTransaction { get; private set; }
    public bool ProbeHadTransaction { get; private set; }
    public bool FreshInputExistedBeforeConflict { get; private set; }
    public int SharedInsertAttempts { get; private set; }
    public Exception? ProbeFailure { get; private set; }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        if (!barrier.IsLosingSave(eventData.Context)
            || !command.CommandText.Contains("INSERT INTO \"BTCPayServer.Plugins.Payjoin\".\"ReceiverSeenInputs\"", StringComparison.Ordinal)
            || !command.Parameters.Cast<DbParameter>().Any(parameter => Equals(parameter.Value, sharedInput.Hash.ToString())))
        {
            return result;
        }

        SharedInsertAttempts++;
        ConflictHadTransaction = command.Transaction is not null;
        if (observePartialInsert)
        {
            try
            {
                using var probe = command.Connection!.CreateCommand();
                probe.Transaction = command.Transaction;
                probe.CommandTimeout = 10;
                probe.CommandText = "SELECT COUNT(*) FROM \"BTCPayServer.Plugins.Payjoin\".\"ReceiverSeenInputs\" "
                    + "WHERE \"TransactionId\" = @txid AND \"OutputIndex\" = @vout";
                var txid = probe.CreateParameter();
                txid.ParameterName = "txid";
                txid.Value = freshInput.Hash.ToString();
                probe.Parameters.Add(txid);
                var vout = probe.CreateParameter();
                vout.ParameterName = "vout";
                vout.Value = (long)freshInput.N;
                probe.Parameters.Add(vout);
                FreshInputExistedBeforeConflict = probe.ExecuteScalar() is long count && count == 1;
                ProbeHadTransaction = probe.Transaction is not null;
            }
            catch (DbException exception)
            {
                ProbeFailure = exception;
            }
        }
        return result;
    }
}
