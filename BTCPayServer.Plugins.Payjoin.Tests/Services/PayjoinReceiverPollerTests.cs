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
            new NoOpAccountingBridgeService(),
            new NoOpAccountingPaymentService(),
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
            new NoOpAccountingBridgeService(),
            new NoOpAccountingPaymentService(),
            new TestLogger<PayjoinReceiverPoller>());

        // Act
        await poller.ProcessTickOnceAsync(CancellationToken.None).ConfigureAwait(true);

        // Assert
        Assert.Equal(1, sessionProcessor.InvocationCount);
        Assert.True(store.TryGetSession(session.InvoiceId, out var reloadedSession));
        Assert.False(reloadedSession!.TryGetContributedInput(out _));
    }

    [Fact]
    public async Task ProcessTickOnceAsyncRunsAccountingReconciliationAfterSessionProcessing()
    {
        // Arrange
        using var testContext = new TestContext();
        var sessionProcessor = new RecordingSessionProcessor();
        var accountingBridgeService = new RecordingAccountingBridgeService();
        var accountingPaymentService = new RecordingAccountingPaymentService();
        using var poller = new PayjoinReceiverPoller(
            testContext.CreateStore(),
            sessionProcessor,
            accountingBridgeService,
            accountingPaymentService,
            new TestLogger<PayjoinReceiverPoller>());

        // Act
        await poller.ProcessTickOnceAsync(CancellationToken.None).ConfigureAwait(true);

        // Assert
        Assert.Equal(1, sessionProcessor.InvocationCount);
        Assert.Equal(1, accountingBridgeService.ExpirePendingInvocationCount);
        Assert.Equal(1, accountingBridgeService.GetPendingInvocationCount);
        Assert.Equal(0, accountingPaymentService.ReconcileInvocationCount);
    }

    [Fact]
    public async Task ProcessTickOnceAsyncLogsPendingBridgeWhenReconciliationProducesNoPaymentUpdate()
    {
        // Arrange
        using var testContext = new TestContext();
        var logger = new TestLogger<PayjoinReceiverPoller>();
        var sessionProcessor = new RecordingSessionProcessor();
        var accountingBridgeService = new RecordingAccountingBridgeService
        {
            PendingBridges =
            [
                new PayjoinAccountingBridgeState(
                    1,
                    "invoice-pending",
                    "store-1",
                    PayjoinConstants.BitcoinCode,
                    "BTC-BTC",
                    null,
                    null,
                    null,
                    null,
                    null,
                    "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    null,
                    null,
                    null,
                    Data.PayjoinAccountingBridgeStatus.PendingFinalTransaction,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    null,
                    DateTimeOffset.UtcNow.AddMinutes(5))
            ]
        };
        var accountingPaymentService = new RecordingAccountingPaymentService();
        using var poller = new PayjoinReceiverPoller(
            testContext.CreateStore(),
            sessionProcessor,
            accountingBridgeService,
            accountingPaymentService,
            logger);

        // Act
        await poller.ProcessTickOnceAsync(CancellationToken.None).ConfigureAwait(true);

        // Assert
        Assert.Equal(1, accountingPaymentService.ReconcileInvocationCount);
        Assert.Contains(logger.Entries, entry => entry.EventId == new EventId(3, "LogPayjoinAccountingBridgePendingWithoutReconciliation"));
    }

    [Fact]
    public async Task ProcessTickOnceAsyncMarksBridgeFailedWhenReconciliationDataIsInvalid()
    {
        // Arrange
        using var testContext = new TestContext();
        var logger = new TestLogger<PayjoinReceiverPoller>();
        var sessionProcessor = new RecordingSessionProcessor();
        var accountingBridgeService = new RecordingAccountingBridgeService
        {
            PendingBridges =
            [
                new PayjoinAccountingBridgeState(
                    1,
                    "invoice-invalid-reconciliation",
                    "store-1",
                    PayjoinConstants.BitcoinCode,
                    "BTC-BTC",
                    null,
                    null,
                    null,
                    null,
                    "0011",
                    "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    0,
                    null,
                    null,
                    Data.PayjoinAccountingBridgeStatus.PendingFinalTransaction,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    null,
                    DateTimeOffset.UtcNow.AddMinutes(5))
            ]
        };
        var accountingPaymentService = new ThrowingAccountingPaymentService(new PayjoinAccountingReconciliationDataException("Invalid settlement script persisted."));
        using var poller = new PayjoinReceiverPoller(
            testContext.CreateStore(),
            sessionProcessor,
            accountingBridgeService,
            accountingPaymentService,
            logger);

        // Act
        await poller.ProcessTickOnceAsync(CancellationToken.None).ConfigureAwait(true);

        // Assert
        Assert.Equal(1, accountingPaymentService.ReconcileInvocationCount);
        Assert.Equal("invoice-invalid-reconciliation", accountingBridgeService.LastMarkedFailedInvoiceId);
        Assert.Equal("Invalid settlement script persisted.", accountingBridgeService.LastMarkedFailedMessage);
        Assert.Contains(logger.Entries, entry =>
            entry.EventId == new EventId(2, "LogPayjoinAccountingReconciliationFailed") &&
            entry.Exception is PayjoinAccountingReconciliationDataException);
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

    private sealed class NoOpAccountingBridgeService : IPayjoinAccountingBridgeService
    {
        public Task<PayjoinAccountingBridgeState> CreateOrGetAsync(CreatePayjoinAccountingBridgeRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PayjoinAccountingBridgeState?> TryGetByInvoiceIdAsync(string invoiceId, CancellationToken cancellationToken) => Task.FromResult<PayjoinAccountingBridgeState?>(null);
        public Task<IReadOnlyCollection<PayjoinAccountingBridgeState>> GetPendingAsync(DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyCollection<PayjoinAccountingBridgeState>>([]);
        public Task<PayjoinAccountingBridgeState?> AttachFallbackAsync(string invoiceId, string fallbackTransactionId, long fallbackOutputIndex, long fallbackValueSats, long effectiveInvoiceValueSats, string? settlementScript, CancellationToken cancellationToken) => Task.FromResult<PayjoinAccountingBridgeState?>(null);
        public Task<PayjoinAccountingBridgeState?> SetSettlementScriptAsync(string invoiceId, string settlementScript, CancellationToken cancellationToken) => Task.FromResult<PayjoinAccountingBridgeState?>(null);
        public Task<PayjoinAccountingBridgeState?> SetExpectedFinalTransactionAsync(string invoiceId, string expectedFinalTransactionId, long? expectedFinalOutputIndex, long? expectedFinalValueSats, CancellationToken cancellationToken) => Task.FromResult<PayjoinAccountingBridgeState?>(null);
        public Task<PayjoinAccountingBridgeState?> MarkReconciledAsync(string invoiceId, string? expectedFinalTransactionId, long? expectedFinalOutputIndex, long? expectedFinalValueSats, DateTimeOffset reconciledAt, CancellationToken cancellationToken) => Task.FromResult<PayjoinAccountingBridgeState?>(null);
        public Task<PayjoinAccountingBridgeState?> MarkFailedAsync(string invoiceId, string failureMessage, CancellationToken cancellationToken) => Task.FromResult<PayjoinAccountingBridgeState?>(null);
        public Task<int> ExpirePendingAsync(DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(0);
    }

    private sealed class NoOpAccountingPaymentService : IPayjoinAccountingPaymentService
    {
        public Task<BTCPayServer.Services.Invoices.PaymentEntity?> ReconcileWithFinalTransactionAsync(PayjoinAccountingBridgeState bridge, CancellationToken cancellationToken) => Task.FromResult<BTCPayServer.Services.Invoices.PaymentEntity?>(null);
    }

    private sealed class RecordingAccountingBridgeService : IPayjoinAccountingBridgeService
    {
        public int ExpirePendingInvocationCount { get; private set; }
        public int GetPendingInvocationCount { get; private set; }
        public IReadOnlyCollection<PayjoinAccountingBridgeState> PendingBridges { get; init; } = [];
        public string? LastMarkedFailedInvoiceId { get; private set; }
        public string? LastMarkedFailedMessage { get; private set; }

        public Task<PayjoinAccountingBridgeState> CreateOrGetAsync(CreatePayjoinAccountingBridgeRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PayjoinAccountingBridgeState?> TryGetByInvoiceIdAsync(string invoiceId, CancellationToken cancellationToken) => Task.FromResult<PayjoinAccountingBridgeState?>(null);
        public Task<IReadOnlyCollection<PayjoinAccountingBridgeState>> GetPendingAsync(DateTimeOffset now, CancellationToken cancellationToken)
        {
            GetPendingInvocationCount++;
            return Task.FromResult(PendingBridges);
        }
        public Task<PayjoinAccountingBridgeState?> AttachFallbackAsync(string invoiceId, string fallbackTransactionId, long fallbackOutputIndex, long fallbackValueSats, long effectiveInvoiceValueSats, string? settlementScript, CancellationToken cancellationToken) => Task.FromResult<PayjoinAccountingBridgeState?>(null);
        public Task<PayjoinAccountingBridgeState?> SetSettlementScriptAsync(string invoiceId, string settlementScript, CancellationToken cancellationToken) => Task.FromResult<PayjoinAccountingBridgeState?>(null);
        public Task<PayjoinAccountingBridgeState?> SetExpectedFinalTransactionAsync(string invoiceId, string expectedFinalTransactionId, long? expectedFinalOutputIndex, long? expectedFinalValueSats, CancellationToken cancellationToken) => Task.FromResult<PayjoinAccountingBridgeState?>(null);
        public Task<PayjoinAccountingBridgeState?> MarkReconciledAsync(string invoiceId, string? expectedFinalTransactionId, long? expectedFinalOutputIndex, long? expectedFinalValueSats, DateTimeOffset reconciledAt, CancellationToken cancellationToken) => Task.FromResult<PayjoinAccountingBridgeState?>(null);
        public Task<PayjoinAccountingBridgeState?> MarkFailedAsync(string invoiceId, string failureMessage, CancellationToken cancellationToken)
        {
            LastMarkedFailedInvoiceId = invoiceId;
            LastMarkedFailedMessage = failureMessage;
            return Task.FromResult<PayjoinAccountingBridgeState?>(null);
        }
        public Task<int> ExpirePendingAsync(DateTimeOffset now, CancellationToken cancellationToken)
        {
            ExpirePendingInvocationCount++;
            return Task.FromResult(0);
        }
    }

    private sealed class RecordingAccountingPaymentService : IPayjoinAccountingPaymentService
    {
        public int ReconcileInvocationCount { get; private set; }

        public Task<BTCPayServer.Services.Invoices.PaymentEntity?> ReconcileWithFinalTransactionAsync(PayjoinAccountingBridgeState bridge, CancellationToken cancellationToken)
        {
            ReconcileInvocationCount++;
            return Task.FromResult<BTCPayServer.Services.Invoices.PaymentEntity?>(null);
        }
    }

    private sealed class ThrowingAccountingPaymentService : IPayjoinAccountingPaymentService
    {
        private readonly Exception _exception;

        public ThrowingAccountingPaymentService(Exception exception)
        {
            _exception = exception;
        }

        public int ReconcileInvocationCount { get; private set; }

        public Task<BTCPayServer.Services.Invoices.PaymentEntity?> ReconcileWithFinalTransactionAsync(PayjoinAccountingBridgeState bridge, CancellationToken cancellationToken)
        {
            ReconcileInvocationCount++;
            throw _exception;
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