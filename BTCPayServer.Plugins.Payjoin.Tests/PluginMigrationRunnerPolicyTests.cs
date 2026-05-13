using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class PluginMigrationRunnerPolicyTests
{
    [Fact]
    public async Task StartAsyncCompletesWhenMigrationStepSucceeds()
    {
        using var testContext = CreateTestContext(MigrationBehavior.Success);

        var exception = await Record.ExceptionAsync(() => testContext.Runner.StartAsync(CancellationToken.None));

        Assert.Null(exception);
        Assert.Equal(1, testContext.Runner.MigrateAsyncCallCount);
    }

    [Fact]
    public async Task StartAsyncSwallowsMigrationStepFailures()
    {
        using var testContext = CreateTestContext(MigrationBehavior.Fail);

        var exception = await Record.ExceptionAsync(() => testContext.Runner.StartAsync(CancellationToken.None));

        Assert.Null(exception);
        Assert.Equal(1, testContext.Runner.MigrateAsyncCallCount);
        var logEntry = Assert.Single(testContext.Logger.Entries);
        Assert.Equal(LogLevel.Error, logEntry.LogLevel);
        Assert.Equal(new EventId(2, "LogPluginMigrationFailed"), logEntry.EventId);
        var loggedException = Assert.IsType<InvalidOperationException>(logEntry.Exception);
        Assert.Equal("Exception", loggedException.Message);
    }

    [Fact]
    public async Task StartAsyncSwallowsRequestedMigrationStepCancellation()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();
        using var testContext = CreateTestContext(MigrationBehavior.Cancel);

        var exception = await Record.ExceptionAsync(() => testContext.Runner.StartAsync(cancellationTokenSource.Token));

        Assert.Null(exception);
        Assert.Equal(1, testContext.Runner.MigrateAsyncCallCount);
    }

    private static RunnerTestContext CreateTestContext(MigrationBehavior migrationBehavior)
    {
        var dbContextFactory = new TestPayjoinPluginDbContextFactory();
        var logger = new TestLogger<PluginMigrationRunner>();
        var runner = new TestPluginMigrationRunner(migrationBehavior, dbContextFactory, logger);
        return new RunnerTestContext(dbContextFactory, runner, logger);
    }

    private sealed class TestPluginMigrationRunner : PluginMigrationRunner
    {
        private readonly MigrationBehavior _migrationBehavior;
        public int MigrateAsyncCallCount { get; private set; }

        public TestPluginMigrationRunner(MigrationBehavior migrationBehavior, PayjoinPluginDbContextFactory pluginDbContextFactory, ILogger<PluginMigrationRunner> logger)
            : base(pluginDbContextFactory, logger)
        {
            _migrationBehavior = migrationBehavior;
        }

        protected internal override Task MigrateAsync(PayjoinPluginDbContext context, CancellationToken cancellationToken)
        {
            _ = context;
            MigrateAsyncCallCount++;

            return _migrationBehavior switch
            {
                MigrationBehavior.Success => Task.CompletedTask,
                MigrationBehavior.Fail => Task.FromException(new InvalidOperationException("Exception")),
                MigrationBehavior.Cancel => Task.FromCanceled(cancellationToken),
                _ => Task.CompletedTask
            };
        }
    }

    private sealed class RunnerTestContext : IDisposable
    {
        private readonly TestPayjoinPluginDbContextFactory _dbContextFactory;

        public RunnerTestContext(TestPayjoinPluginDbContextFactory dbContextFactory, TestPluginMigrationRunner runner, TestLogger<PluginMigrationRunner> logger)
        {
            _dbContextFactory = dbContextFactory;
            Runner = runner;
            Logger = logger;
        }

        public TestPluginMigrationRunner Runner { get; }

        public TestLogger<PluginMigrationRunner> Logger { get; }

        public void Dispose()
        {
            _dbContextFactory.Dispose();
        }
    }

    private sealed class TestPayjoinPluginDbContextFactory : PayjoinPluginDbContextFactory
    {
        private readonly List<string> _databaseNames = [];

        public TestPayjoinPluginDbContextFactory()
            : base(Options.Create(new DatabaseOptions
            {
                ConnectionString = "Host=localhost;Database=payjoin-plugin-tests;Username=postgres"
            }))
        {
        }

        public override PayjoinPluginDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptionsAction = null)
        {
            var databaseName = $"payjoin-plugin-migration-runner-tests-{Guid.NewGuid():N}";
            _databaseNames.Add(databaseName);
            var options = new DbContextOptionsBuilder<PayjoinPluginDbContext>()
                .UseInMemoryDatabase(databaseName)
                .Options;
            return new PayjoinPluginDbContext(options);
        }

        public void Dispose()
        {
            foreach (var databaseName in _databaseNames)
            {
                var options = new DbContextOptionsBuilder<PayjoinPluginDbContext>()
                    .UseInMemoryDatabase(databaseName)
                    .Options;
                using var context = new PayjoinPluginDbContext(options);
                context.Database.EnsureDeleted();
            }
        }
    }

    private enum MigrationBehavior
    {
        Success,
        Fail,
        Cancel
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, eventId, exception));
        }

        public sealed record LogEntry(LogLevel LogLevel, EventId EventId, Exception? Exception);

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}
