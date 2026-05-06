using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBXplorer;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using NSubstitute;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinReceiverInputSelectorTests
{
    [Fact]
    public async Task TryGetPersistedContributedCoinsAsyncReturnsNullWhenOutPointMissing()
    {
        using var context = new TestContext();
        var selector = CreateSelector();
        var session = CreateSession();

        var result = await selector.TryGetPersistedContributedCoinsAsync(session, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryGetPersistedContributedCoinsAsyncReturnsNullWhenContributedInputTransactionIdIsInvalid()
    {
        using var context = new TestContext();
        var selector = CreateSelector();
        var session = CreateSession(
            contributedInputTransactionId: "not-a-transaction-id",
            contributedInputOutputIndex: 0);

        var result = await selector.TryGetPersistedContributedCoinsAsync(session, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryGetPersistedContributedCoinsAsyncReturnsNullWhenContributedInputOutputIndexIsNegative()
    {
        using var context = new TestContext();
        var selector = CreateSelector();
        var session = CreateSession(
            contributedInputTransactionId: uint256.One.ToString(),
            contributedInputOutputIndex: -1);

        var result = await selector.TryGetPersistedContributedCoinsAsync(session, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryGetPersistedContributedCoinsAsyncReturnsNullWhenContributedInputOutputIndexOverflowsUInt()
    {
        using var context = new TestContext();
        var selector = CreateSelector();
        var session = CreateSession(
            contributedInputTransactionId: uint256.One.ToString(),
            contributedInputOutputIndex: (long)uint.MaxValue + 1);

        var result = await selector.TryGetPersistedContributedCoinsAsync(session, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryGetPersistedContributedCoinsAsyncReturnsNullWhenNetworkUnavailable()
    {
        using var context = new TestContext();
        var selector = CreateSelector(CreateEmptyNetworkProvider());
        var outPoint = new OutPoint(uint256.Parse("8888888888888888888888888888888888888888888888888888888888888888"), 2);
        var session = CreateSession(
            contributedInputTransactionId: outPoint.Hash.ToString(),
            contributedInputOutputIndex: outPoint.N);

        var result = await selector.TryGetPersistedContributedCoinsAsync(session, CancellationToken.None);

        Assert.Null(result);
    }

    private static PayjoinReceiverInputSelector CreateSelector(BTCPayNetworkProvider? networkProvider = null)
    {
        return new PayjoinReceiverInputSelector(
            networkProvider ?? CreateEmptyNetworkProvider(),
            new PayjoinAvailabilityService(null!, null!, null!));
    }

    private static PayjoinReceiverSessionState CreateSession(
        string? invoiceId = null,
        string? storeId = null,
        string? receiverAddress = null,
        Uri? ohttpRelayUrl = null,
        DateTimeOffset? monitoringExpiresAt = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? updatedAt = null,
        bool isCloseRequested = false,
        InvoiceStatus? closeInvoiceStatus = null,
        DateTimeOffset? closeRequestedAt = null,
        bool initializedPollAfterCloseRequestConsumed = false,
        string? contributedInputTransactionId = null,
        long? contributedInputOutputIndex = null,
        IEnumerable<string>? events = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new PayjoinReceiverSessionState(
            invoiceId ?? "invoice-1",
            storeId ?? "store-1",
            receiverAddress ?? "bcrt1qexampleaddress0000000000000000000000000",
            ohttpRelayUrl ?? new Uri("https://relay.example/"),
            monitoringExpiresAt ?? now.AddHours(1),
            createdAt ?? now,
            updatedAt ?? now,
            isCloseRequested,
            closeInvoiceStatus,
            closeRequestedAt,
            initializedPollAfterCloseRequestConsumed,
            contributedInputTransactionId,
            contributedInputOutputIndex,
            events);
    }

    private static BTCPayNetworkProvider CreateEmptyNetworkProvider()
    {
        return new BTCPayNetworkProvider(
            Array.Empty<BTCPayNetworkBase>(),
            Substitute.For<NBXplorerNetworkProvider>(ChainName.Regtest),
            Substitute.For<BTCPayServer.Logging.Logs>());
    }

    private sealed class TestContext : IDisposable
    {
        private readonly TestPayjoinPluginDbContextFactory _dbContextFactory = new();

        public PayjoinReceiverSessionStore CreateStore() => new(_dbContextFactory);

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
            : base(Options.Create(new DatabaseOptions
            {
                ConnectionString = "Host=localhost;Database=payjoin-plugin-tests;Username=postgres"
            }))
        {
            var databaseName = $"payjoin-input-selector-tests-{Guid.NewGuid():N}";
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
}
