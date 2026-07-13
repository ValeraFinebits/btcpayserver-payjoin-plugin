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

    [Fact]
    public async Task TryContributeInputsAsyncContributesTheSelectedCandidate()
    {
        using var context = new TestContext();
        var store = context.CreateStore();
        CreatePersistedSession(store, "invoice-1");
        var candidates = CreateCandidates(2);
        var selector = CreateSelector(
            store,
            new TestReceiverWalletAdapter { Candidates = candidates },
            new TestProposalOperations());

        var result = await selector.TryContributeInputsAsync(null!, "store-1", "invoice-1", DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);

        Assert.True(string.IsNullOrEmpty(result.FailureMessage));
        Assert.NotNull(result.ContributedCoins);
        Assert.Equal(candidates[0].Coin.OutPoint, Assert.Single(result.ContributedCoins!).OutPoint);
    }

    [Fact]
    public async Task TryContributeInputsAsyncReselectsWhenReservationConflicts()
    {
        using var context = new TestContext();
        var store = context.CreateStore();
        CreatePersistedSession(store, "invoice-1");
        CreatePersistedSession(store, "other-invoice");
        var candidates = CreateCandidates(2);
        Assert.True(store.TryReserveContributedInput("store-1", "other-invoice", candidates[0].Coin.OutPoint, DateTimeOffset.UtcNow.AddHours(1)));
        var selector = CreateSelector(
            store,
            new TestReceiverWalletAdapter { Candidates = candidates },
            new TestProposalOperations());

        var result = await selector.TryContributeInputsAsync(null!, "store-1", "invoice-1", DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);

        Assert.True(string.IsNullOrEmpty(result.FailureMessage));
        Assert.NotNull(result.ContributedCoins);
        Assert.Equal(candidates[1].Coin.OutPoint, Assert.Single(result.ContributedCoins!).OutPoint);
    }

    [Fact]
    public async Task TryContributeInputsAsyncFailsWhenEveryCandidateIsReserved()
    {
        using var context = new TestContext();
        var store = context.CreateStore();
        CreatePersistedSession(store, "invoice-1");
        var candidates = CreateCandidates(2);
        for (var i = 0; i < candidates.Count; i++)
        {
            var otherInvoiceId = $"other-invoice-{i}";
            CreatePersistedSession(store, otherInvoiceId);
            Assert.True(store.TryReserveContributedInput("store-1", otherInvoiceId, candidates[i].Coin.OutPoint, DateTimeOffset.UtcNow.AddHours(1)));
        }

        var selector = CreateSelector(
            store,
            new TestReceiverWalletAdapter { Candidates = candidates },
            new TestProposalOperations());

        var result = await selector.TryContributeInputsAsync(null!, "store-1", "invoice-1", DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);

        Assert.Null(result.ContributedCoins);
        Assert.Contains($"candidate '{candidates[0].Coin.OutPoint}' reservation conflict", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains($"candidate '{candidates[1].Coin.OutPoint}' reservation conflict", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryContributeInputsAsyncReselectsWhenContributionIsRejected()
    {
        using var context = new TestContext();
        var store = context.CreateStore();
        CreatePersistedSession(store, "invoice-1");
        var candidates = CreateCandidates(2);
        var operations = new TestProposalOperations
        {
            RejectedOutPoints = { candidates[0].Coin.OutPoint }
        };
        var selector = CreateSelector(
            store,
            new TestReceiverWalletAdapter { Candidates = candidates },
            operations);

        var result = await selector.TryContributeInputsAsync(null!, "store-1", "invoice-1", DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);

        Assert.True(string.IsNullOrEmpty(result.FailureMessage));
        Assert.NotNull(result.ContributedCoins);
        Assert.Equal(candidates[1].Coin.OutPoint, Assert.Single(result.ContributedCoins!).OutPoint);
    }

    [Fact]
    public async Task TryContributeInputsAsyncFailsWhenSelectionCannotBeMappedBackToACoin()
    {
        using var context = new TestContext();
        var candidates = CreateCandidates(1);
        var selector = CreateSelector(
            context.CreateStore(),
            new TestReceiverWalletAdapter { Candidates = candidates, ResolveToNull = true },
            new TestProposalOperations());

        var result = await selector.TryContributeInputsAsync(null!, "store-1", "invoice-1", DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);

        Assert.Null(result.ContributedCoins);
        Assert.Contains("selected receiver input could not be mapped back to a wallet coin", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryContributeInputsAsyncFailsWhenTheLibraryCannotSelect()
    {
        using var context = new TestContext();
        var candidates = CreateCandidates(1);
        var selector = CreateSelector(
            context.CreateStore(),
            new TestReceiverWalletAdapter { Candidates = candidates },
            new TestProposalOperations { SelectionFailureMessage = "no viable input" });

        var result = await selector.TryContributeInputsAsync(null!, "store-1", "invoice-1", DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);

        Assert.Null(result.ContributedCoins);
        Assert.Contains("receiver input selection failed: no viable input", result.FailureMessage, StringComparison.Ordinal);
    }

    private static void CreatePersistedSession(PayjoinReceiverSessionStore store, string invoiceId)
    {
        store.CreateSession(
            invoiceId,
            "bcrt1qexampleaddress0000000000000000000000000",
            "store-1",
            DateTimeOffset.UtcNow.AddHours(1),
            ["bootstrap-event"]);
    }

    private static List<PayjoinReceiverInputCandidate> CreateCandidates(int count)
    {
        var candidates = new List<PayjoinReceiverInputCandidate>();
        for (var i = 0; i < count; i++)
        {
            var hash = uint256.Parse($"{i + 1:x64}");
            var coin = new ReceivedCoin { OutPoint = new OutPoint(hash, (uint)i) };
            candidates.Add(new PayjoinReceiverInputCandidate(null!, coin));
        }

        return candidates;
    }

    private static PayjoinReceiverInputSelector CreateSelector(
        PayjoinReceiverSessionStore sessionStore,
        IPayjoinReceiverWalletAdapter? walletAdapter = null,
        IPayjoinReceiverInputProposalOperations? proposalOperations = null)
    {
        return new PayjoinReceiverInputSelector(
            walletAdapter ?? new TestReceiverWalletAdapter(),
            proposalOperations ?? new TestProposalOperations(),
            sessionStore);
    }

    private static PayjoinReceiverSessionState CreateSession(
        string? invoiceId = null,
        string? storeId = null,
        string? receiverAddress = null,
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

        public List<PayjoinReceiverInputCandidate> Candidates { get; init; } = new();

        public bool ResolveToNull { get; init; }

        public Task<IReadOnlyList<PayjoinReceiverInputCandidate>> GetInputCandidatesAsync(
            string storeId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<PayjoinReceiverInputCandidate>>(Candidates);
        }

        public PayjoinReceiverInputCandidate? ResolveSelectedCandidate(
            IReadOnlyList<PayjoinReceiverInputCandidate> candidates,
            global::Payjoin.OutPoint selectedOutPoint)
        {
            if (ResolveToNull)
            {
                return null;
            }

            return candidates.SingleOrDefault(candidate =>
                string.Equals(candidate.Coin.OutPoint.Hash.ToString(), selectedOutPoint.Txid, StringComparison.OrdinalIgnoreCase) &&
                candidate.Coin.OutPoint.N == selectedOutPoint.Vout);
        }

        public Task<ReceivedCoin[]> GetConfirmedReceiverCoinsAsync(
            string storeId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(ConfirmedCoins);
        }
    }

    private sealed class TestProposalOperations : IPayjoinReceiverInputProposalOperations
    {
        public string? SelectionFailureMessage { get; init; }

        public HashSet<OutPoint> RejectedOutPoints { get; } = new();

        public global::Payjoin.OutPoint SelectPreservingPrivacy(
            global::Payjoin.WantsInputs proposal,
            IReadOnlyList<PayjoinReceiverInputCandidate> candidates)
        {
            if (SelectionFailureMessage is not null)
            {
                throw new PayjoinReceiverInputSelectionException(SelectionFailureMessage);
            }

            var first = candidates[0].Coin.OutPoint;
            return new global::Payjoin.OutPoint(first.Hash.ToString(), first.N);
        }

        public global::Payjoin.WantsInputs ContributeInputs(
            global::Payjoin.WantsInputs proposal,
            PayjoinReceiverInputCandidate selected)
        {
            if (RejectedOutPoints.Contains(selected.Coin.OutPoint))
            {
                throw new PayjoinReceiverInputContributionException($"input rejected");
            }

            return null!;
        }
    }
}
