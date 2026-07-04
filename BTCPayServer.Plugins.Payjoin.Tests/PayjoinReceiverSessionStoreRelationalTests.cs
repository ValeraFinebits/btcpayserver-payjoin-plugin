using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NBitcoin;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class PayjoinReceiverSessionStoreRelationalTests
{
    [Fact]
    public void MarkSeenAndWasPresentReportsRepeatedOutpointsAsSeen()
    {
        // Arrange
        using var testContext = new RelationalTestContext();
        var store = testContext.CreateSeenInputStore();
        var transactionId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        // Act + Assert: first sighting is new, repeat is reported as seen, a different vout is new again.
        Assert.False(store.MarkSeenAndWasPresent(transactionId, 0));
        Assert.True(store.MarkSeenAndWasPresent(transactionId, 0));
        Assert.False(store.MarkSeenAndWasPresent(transactionId, 1));

        using var context = testContext.CreateDbContext();
        Assert.Equal(2, context.ReceiverSeenInputs.Count());
    }

    [Fact]
    public void TryReserveContributedInputAllowsOnlyOneReservationPerOutPointOnRelationalProvider()
    {
        // Arrange
        using var testContext = new RelationalTestContext();
        var firstStore = testContext.CreateStore();
        var secondStore = testContext.CreateStore();
        var firstSession = CreateSession(firstStore, "invoice-relational-first");
        var secondSession = CreateSession(secondStore, "invoice-relational-second");
        var outPoint = new OutPoint(uint256.Parse("dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"), 1);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);

        // Act
        Assert.True(firstStore.TryReserveContributedInput(firstSession.StoreId, firstSession.InvoiceId, outPoint, expiresAt));
        Assert.False(secondStore.TryReserveContributedInput(secondSession.StoreId, secondSession.InvoiceId, outPoint, expiresAt));

        // Assert
        using var context = testContext.CreateDbContext();
        var reservation = Assert.Single(context.ReceiverInputReservations);
        Assert.Equal(firstSession.InvoiceId, reservation.InvoiceId);
        Assert.Equal(outPoint.Hash.ToString(), reservation.TransactionId);
        Assert.Equal((long)outPoint.N, reservation.OutputIndex);
    }

    [Fact]
    public async Task TryReserveContributedInputAllowsOnlyOneWinnerUnderConcurrentRequestsOnRelationalProvider()
    {
        // Arrange
        using var testContext = new RelationalTestContext();
        var firstStore = testContext.CreateStore();
        var secondStore = testContext.CreateStore();
        var firstSession = CreateSession(firstStore, "invoice-relational-concurrent-first");
        var secondSession = CreateSession(secondStore, "invoice-relational-concurrent-second");
        var outPoint = new OutPoint(uint256.Parse("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), 2);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);

        using var start = new ManualResetEventSlim(false);
        var firstAttempt = Task.Run(() =>
        {
            start.Wait();
            return firstStore.TryReserveContributedInput(firstSession.StoreId, firstSession.InvoiceId, outPoint, expiresAt);
        });
        var secondAttempt = Task.Run(() =>
        {
            start.Wait();
            return secondStore.TryReserveContributedInput(secondSession.StoreId, secondSession.InvoiceId, outPoint, expiresAt);
        });

        // Act
        start.Set();
        var results = await Task.WhenAll(firstAttempt, secondAttempt).ConfigureAwait(true);

        // Assert
        Assert.Single(results, result => result);
        Assert.Single(results, result => !result);

        using var context = testContext.CreateDbContext();
        var reservation = Assert.Single(context.ReceiverInputReservations);
        Assert.Equal(outPoint.Hash.ToString(), reservation.TransactionId);
        Assert.Equal((long)outPoint.N, reservation.OutputIndex);
        Assert.Contains(reservation.InvoiceId, new[] { firstSession.InvoiceId, secondSession.InvoiceId });
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

    private sealed class RelationalTestContext : IDisposable
    {
        private readonly TestPayjoinPluginDbContextFactory _dbContextFactory;
        private readonly SqliteUniqueConstraintViolationDetector _uniqueConstraintViolationDetector = new();

        public RelationalTestContext()
        {
            _dbContextFactory = new TestPayjoinPluginDbContextFactory();
        }

        public PayjoinReceiverSessionStore CreateStore() => new(_dbContextFactory, _uniqueConstraintViolationDetector);

        public PayjoinSeenInputStore CreateSeenInputStore() => new(_dbContextFactory, _uniqueConstraintViolationDetector);

        public PayjoinPluginDbContext CreateDbContext() => _dbContextFactory.CreateContext();

        public void Dispose()
        {
            using var context = _dbContextFactory.CreateContext();
            context.Database.EnsureDeleted();
            _dbContextFactory.Dispose();
        }
    }

    private sealed class SqliteUniqueConstraintViolationDetector : IPayjoinUniqueConstraintViolationDetector
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

    private sealed class TestPayjoinPluginDbContextFactory : PayjoinPluginDbContextFactory, IDisposable
    {
        private readonly string _connectionString;
        private readonly SqliteConnection _keeperConnection;

        public TestPayjoinPluginDbContextFactory()
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

        [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The created SQLite connection is owned and disposed by SqliteOwnedPayjoinPluginDbContext.")]
        public override PayjoinPluginDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptionsAction = null)
        {
            var connection = new SqliteConnection(_connectionString);
            try
            {
                connection.Open();
                var dbContextOptions = new DbContextOptionsBuilder<PayjoinPluginDbContext>()
                    .UseSqlite(connection, sqliteOptions => sqliteOptions.CommandTimeout(30))
                    .Options;

                return new SqliteOwnedPayjoinPluginDbContext(dbContextOptions, connection);
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

        private sealed class SqliteOwnedPayjoinPluginDbContext : PayjoinPluginDbContext
        {
            private readonly SqliteConnection _connection;

            public SqliteOwnedPayjoinPluginDbContext(DbContextOptions<PayjoinPluginDbContext> options, SqliteConnection connection)
                : base(options)
            {
                _connection = connection;
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
}
