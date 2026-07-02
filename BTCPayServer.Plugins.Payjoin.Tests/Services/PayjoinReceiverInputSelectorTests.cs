using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services.Wallets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using NBitcoin;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinReceiverInputSelectorTests
{
    [Fact]
    public async Task TryGetPersistedContributedCoinsAsyncReturnsNullWhenOutPointMissing()
    {
        using var context = new TestContext();
        var selector = CreateSelector(context.CreateStore());
        var session = CreateSession();

        var result = await selector.TryGetPersistedContributedCoinsAsync(session, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryGetPersistedContributedCoinsAsyncReturnsNullWhenContributedInputTransactionIdIsInvalid()
    {
        using var context = new TestContext();
        var selector = CreateSelector(context.CreateStore());
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
        var selector = CreateSelector(context.CreateStore());
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
        var selector = CreateSelector(context.CreateStore());
        var session = CreateSession(
            contributedInputTransactionId: uint256.One.ToString(),
            contributedInputOutputIndex: (long)uint.MaxValue + 1);

        var result = await selector.TryGetPersistedContributedCoinsAsync(session, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task TryGetPersistedContributedCoinsAsyncReturnsNullWhenCoinUnavailable()
    {
        using var context = new TestContext();
        var selector = CreateSelector(context.CreateStore());
        var outPoint = new OutPoint(uint256.Parse("8888888888888888888888888888888888888888888888888888888888888888"), 2);
        var session = CreateSession(
            contributedInputTransactionId: outPoint.Hash.ToString(),
            contributedInputOutputIndex: outPoint.N);

        var result = await selector.TryGetPersistedContributedCoinsAsync(session, CancellationToken.None);

        Assert.Null(result);
    }

    private static PayjoinReceiverInputSelector CreateSelector(
        PayjoinReceiverSessionStore sessionStore,
        IPayjoinReceiverWalletAdapter? walletAdapter = null)
    {
        return new PayjoinReceiverInputSelector(
            walletAdapter ?? new TestReceiverWalletAdapter(),
            sessionStore);
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

    private sealed class TestReceiverWalletAdapter : IPayjoinReceiverWalletAdapter
    {
        public ReceivedCoin[] ConfirmedCoins { get; init; } = Array.Empty<ReceivedCoin>();

        public Task<IReadOnlyList<PayjoinReceiverInputCandidate>> GetInputCandidatesAsync(
            string storeId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<PayjoinReceiverInputCandidate>>(Array.Empty<PayjoinReceiverInputCandidate>());
        }

        public PayjoinReceiverInputCandidate? ResolveSelectedCandidate(
            IReadOnlyList<PayjoinReceiverInputCandidate> candidates,
            global::Payjoin.OutPoint selectedOutPoint)
        {
            return null;
        }

        public Task<ReceivedCoin[]> GetConfirmedReceiverCoinsAsync(
            string storeId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(ConfirmedCoins);
        }
    }
}
