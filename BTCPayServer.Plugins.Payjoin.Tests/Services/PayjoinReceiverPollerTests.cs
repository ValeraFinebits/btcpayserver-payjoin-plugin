using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinReceiverPollerTests
{
    [Fact]
    public async Task ExecuteAsyncLogsAndContinuesAfterGetSessionsFailure()
    {
        // Arrange
        var logger = new TestLogger<PayjoinReceiverPoller>();
        using var testContext = new TestContext();
        var sessionProcessor = new ThrowingOnceSessionProcessor();
        using var poller = new PayjoinReceiverPoller(
            testContext.CreateStore(),
            sessionProcessor,
            logger);
        using var cancellationTokenSource = new CancellationTokenSource();

        // Act
        await poller.StartAsync(cancellationTokenSource.Token).ConfigureAwait(true);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(12);
        while (!sessionProcessor.HasSuccessfulExecution && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), global::Xunit.TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
        cancellationTokenSource.Cancel();
        await poller.StopAsync(CancellationToken.None).ConfigureAwait(true);

        // Assert
        var logEntry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, logEntry.LogLevel);
        Assert.Equal(new EventId(1, "LogPayjoinReceiverPollingFailed"), logEntry.EventId);
        var exception = Assert.IsType<InvalidOperationException>(logEntry.Exception);
        Assert.Equal("Simulated session load failure.", exception.Message);
        Assert.True(sessionProcessor.HasSuccessfulExecution);
    }

    [Fact]
    public async Task ProcessTickOnceAsyncCleansExpiredReservationsBeforeProcessingSessions()
    {
        // Arrange
        using var testContext = new TestContext();
        var store = testContext.CreateStore();
        var session = CreateSession(store, "invoice-expired-cleanup");
        var outPoint = new OutPoint(uint256.Parse("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), 1);
        Assert.True(store.TryReserveContributedInput(session.StoreId, session.InvoiceId, outPoint, DateTimeOffset.UtcNow.AddMinutes(-1)));

        var sessionProcessor = new RecordingSessionProcessor();
        using var poller = new PayjoinReceiverPoller(
            store,
            sessionProcessor,
            new TestLogger<PayjoinReceiverPoller>());

        // Act
        await poller.ProcessTickOnceAsync(CancellationToken.None).ConfigureAwait(true);

        // Assert
        Assert.Equal(1, sessionProcessor.InvocationCount);
        Assert.True(store.TryGetSession(session.InvoiceId, out var reloadedSession));
        Assert.False(reloadedSession!.TryGetContributedInput(out _));
    }

    private sealed class ThrowingOnceSessionProcessor : IPayjoinReceiverSessionProcessor
    {
        private int _hasThrown;

        public bool HasSuccessfulExecution { get; private set; }

        public async Task ProcessTickAsync(CancellationToken stoppingToken)
        {
            await Task.Yield();
            if (Interlocked.Exchange(ref _hasThrown, 1) == 0)
            {
                throw new InvalidOperationException("Simulated session load failure.");
            }

            HasSuccessfulExecution = true;
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
    }

    private sealed class RecordingSessionProcessor : IPayjoinReceiverSessionProcessor
    {
        public int InvocationCount { get; private set; }

        public Task ProcessTickAsync(CancellationToken stoppingToken)
        {
            InvocationCount++;
            return Task.CompletedTask;
        }
    }

    private static PayjoinReceiverSessionState CreateSession(PayjoinReceiverSessionStore store, string invoiceId)
    {
        return store.CreateSession(
            invoiceId,
            "bcrt1qexampleaddress0000000000000000000000000",
            "store-1",
            new Uri("https://relay.example/"),
            DateTimeOffset.UtcNow.AddMinutes(15),
            ["bootstrap-event"]);
    }

    private sealed class TestContext : IDisposable
    {
        private readonly TestPayjoinPluginDbContextFactory _dbContextFactory = new();
        private readonly PostgresPayjoinUniqueConstraintViolationDetector _uniqueConstraintViolationDetector = new();

        public PayjoinReceiverSessionStore CreateStore() => new(_dbContextFactory, _uniqueConstraintViolationDetector);

        public void Dispose()
        {
            using var db = _dbContextFactory.CreateContext();
            db.Database.EnsureDeleted();
        }
    }

    private sealed class TestPayjoinPluginDbContextFactory : PayjoinPluginDbContextFactory
    {
        private static readonly InMemoryDatabaseRoot SharedDatabaseRoot = new();
        private readonly DbContextOptions<PayjoinPluginDbContext> _dbContextOptions;

        public TestPayjoinPluginDbContextFactory()
            : base(Options.Create(new global::BTCPayServer.Abstractions.Models.DatabaseOptions
            {
                ConnectionString = "Host=localhost;Database=payjoin-plugin-tests;Username=postgres"
            }))
        {
            var databaseName = $"payjoin-poller-tests-{Guid.NewGuid():N}";
            _dbContextOptions = new DbContextOptionsBuilder<PayjoinPluginDbContext>()
                .UseInMemoryDatabase(databaseName, SharedDatabaseRoot)
                .Options;

            using var db = CreateContext();
            db.Database.EnsureCreated();
        }

        public override PayjoinPluginDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptionsAction = null)
        {
            return new PayjoinPluginDbContext(_dbContextOptions);
        }
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = new();

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
