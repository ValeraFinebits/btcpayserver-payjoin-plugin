using BTCPayServer.Abstractions.Models;
using BTCPayServer.Logging;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Wallets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBXplorer;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using System.Collections.Concurrent;
using Xunit;
using HasReplyableError = Payjoin.HasReplyableError;
using Initialized = Payjoin.Initialized;
using MaybeInputsOwned = Payjoin.MaybeInputsOwned;
using MaybeInputsSeen = Payjoin.MaybeInputsSeen;
using OutputsUnknown = Payjoin.OutputsUnknown;
using PayjoinProposal = Payjoin.PayjoinProposal;
using ProvisionalProposal = Payjoin.ProvisionalProposal;
using UncheckedOriginalPayload = Payjoin.UncheckedOriginalPayload;
using WantsFeeRange = Payjoin.WantsFeeRange;
using WantsInputs = Payjoin.WantsInputs;
using WantsOutputs = Payjoin.WantsOutputs;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinReceiverSessionProcessorTests
{
    [Fact]
    public void ResolveFallbackReceiverOutputReturnsMatchingReceiverOutputWhenItIsNotAtIndexZero()
    {
        using var receiverKey = new Key();
        using var otherKey = new Key();
        var fallbackTransaction = Network.RegTest.CreateTransaction();
        fallbackTransaction.Outputs.Add(Money.Satoshis(10_000), otherKey.PubKey.WitHash.ScriptPubKey);
        fallbackTransaction.Outputs.Add(Money.Satoshis(20_000), receiverKey.PubKey.WitHash.ScriptPubKey);

        var match = PayjoinReceiverSessionProcessor.ResolveFallbackReceiverOutput(fallbackTransaction, receiverKey.PubKey.WitHash.ScriptPubKey.ToBytes());

        Assert.True(match.Success);
        Assert.Equal(PayjoinReceiverSessionProcessor.FallbackReceiverOutputMatchStatus.Found, match.Status);
        Assert.Equal(1U, match.OutputIndex);
        Assert.Equal(20_000, match.ValueSats);
    }

    [Fact]
    public void ResolveFallbackReceiverOutputReturnsNotFoundWhenNoReceiverOutputMatches()
    {
        using var receiverKey = new Key();
        using var otherKey = new Key();
        var fallbackTransaction = Network.RegTest.CreateTransaction();
        fallbackTransaction.Outputs.Add(Money.Satoshis(10_000), otherKey.PubKey.WitHash.ScriptPubKey);

        var match = PayjoinReceiverSessionProcessor.ResolveFallbackReceiverOutput(fallbackTransaction, receiverKey.PubKey.WitHash.ScriptPubKey.ToBytes());

        Assert.False(match.Success);
        Assert.Equal(PayjoinReceiverSessionProcessor.FallbackReceiverOutputMatchStatus.NotFound, match.Status);
        Assert.Null(match.OutputIndex);
        Assert.Null(match.ValueSats);
    }

    [Fact]
    public void ResolveFallbackReceiverOutputReturnsAmbiguousWhenMultipleReceiverOutputsMatch()
    {
        using var receiverKey = new Key();
        var fallbackTransaction = Network.RegTest.CreateTransaction();
        var receiverScript = receiverKey.PubKey.WitHash.ScriptPubKey;
        fallbackTransaction.Outputs.Add(Money.Satoshis(10_000), receiverScript);
        fallbackTransaction.Outputs.Add(Money.Satoshis(20_000), receiverScript);

        var match = PayjoinReceiverSessionProcessor.ResolveFallbackReceiverOutput(fallbackTransaction, receiverScript.ToBytes());

        Assert.False(match.Success);
        Assert.Equal(PayjoinReceiverSessionProcessor.FallbackReceiverOutputMatchStatus.Ambiguous, match.Status);
        Assert.Null(match.OutputIndex);
        Assert.Null(match.ValueSats);
    }

    [Fact]
    public async Task ProcessTickAsyncDoesNotOwnExpiredReservationCleanup()
    {
        // Arrange
        using var testContext = new TestContext();
        var store = testContext.CreateStore();
        var session = CreateSession(store, "invoice-expired-cleanup");
        var outPoint = new OutPoint(uint256.Parse("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), 1);
        Assert.True(store.TryReserveContributedInput(session.StoreId, session.InvoiceId, outPoint, DateTimeOffset.UtcNow.AddMinutes(-1)));

        var guard = new RecordingSessionGuard();
        var processor = CreateProcessor(store, guard);

        // Act
        await processor.ProcessTickAsync(CancellationToken.None);

        // Assert
        Assert.True(store.TryGetSession(session.InvoiceId, out var reloadedSession));
        Assert.True(reloadedSession!.TryGetContributedInput(out _));
        Assert.Contains(session.InvoiceId, guard.VisitedInvoiceIds);
    }

    [Fact]
    public async Task ProcessTickAsyncIsolatesInvalidOperationFailuresPerSession()
    {
        // Arrange
        using var testContext = new TestContext();
        var store = testContext.CreateStore();
        var failingSession = CreateSession(store, "invoice-failing");
        var survivingSession = CreateSession(store, "invoice-surviving");
        var guard = new SelectiveGuard(failingSession.InvoiceId);
        var processor = CreateProcessor(store, guard);

        // Act
        await processor.ProcessTickAsync(CancellationToken.None);

        // Assert
        Assert.False(store.TryGetSession(failingSession.InvoiceId, out _));
        Assert.True(store.TryGetSession(survivingSession.InvoiceId, out var reloadedSurvivingSession));
        Assert.NotNull(reloadedSurvivingSession);
        Assert.Contains(failingSession.InvoiceId, guard.VisitedInvoiceIds);
        Assert.Contains(survivingSession.InvoiceId, guard.VisitedInvoiceIds);
    }

    private static PayjoinReceiverSessionProcessor CreateProcessor(
        PayjoinReceiverSessionStore sessionStore,
        IPayjoinReceiverSessionGuard sessionGuard)
    {
        var nbxplorerNetworkProvider = new NBXplorerNetworkProvider(ChainName.Regtest);
        var network = new BTCPayNetwork
        {
            CryptoCode = PayjoinConstants.BitcoinCode,
            DisplayName = "Bitcoin",
            NBXplorerNetwork = nbxplorerNetworkProvider.GetFromCryptoCode(PayjoinConstants.BitcoinCode),
            CryptoImagePath = "imlegacy/bitcoin.svg",
            LightningImagePath = "imlegacy/bitcoin-lightning.svg",
            DefaultSettings = new BTCPayDefaultSettings(),
            CoinType = new KeyPath("1'"),
            SupportRBF = true,
            SupportPayJoin = true,
            VaultSupported = true
        }.SetDefaultElectrumMapping(ChainName.Regtest);
        var networkProvider = new BTCPayNetworkProvider([network], nbxplorerNetworkProvider, new Logs());

        return new PayjoinReceiverSessionProcessor(
            sessionStore,
            sessionGuard,
            new NoOpStateProcessor(),
            new NoOpOutputBuilder(),
            new NoOpInputSelector(),
            new NoOpAccountingBridgeService(),
            new NoOpAccountingPaymentService(),
            new NoOpInvoiceLookup(),
            new NoOpProposalFinalizer(),
            networkProvider,
            NullLogger<PayjoinReceiverSessionProcessor>.Instance);
    }

    private static PayjoinReceiverSessionState CreateSession(PayjoinReceiverSessionStore store, string invoiceId)
    {
        return store.CreateSession(
            invoiceId,
            "bcrt1qexampleaddress0000000000000000000000000",
            "store-1",
            new global::System.Uri("https://relay.example/"),
            DateTimeOffset.UtcNow.AddMinutes(15),
            ["bootstrap-event"]);
    }

    private sealed class RecordingSessionGuard : IPayjoinReceiverSessionGuard
    {
        public ConcurrentBag<string> VisitedInvoiceIds { get; } = [];

        public Task<PayjoinReceiverSessionGuardResult?> TryPrepareAsync(PayjoinReceiverSessionState session, CancellationToken cancellationToken)
        {
            VisitedInvoiceIds.Add(session.InvoiceId);
            return Task.FromResult<PayjoinReceiverSessionGuardResult?>(null);
        }
    }

    private sealed class NoOpStateProcessor : IPayjoinReceiverStateProcessor
    {
        public Task ProcessInitializedAsync(PayjoinReceiverStateContext context, Initialized initialized, Func<WantsOutputs, PayjoinReceiverStateContext, CancellationToken, Task> continueWithOutputsAsync, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ProcessReplyableErrorAsync(PayjoinReceiverStateContext context, HasReplyableError replyableError, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ProcessUncheckedProposalAsync(PayjoinReceiverStateContext context, UncheckedOriginalPayload proposal, Func<WantsOutputs, PayjoinReceiverStateContext, CancellationToken, Task> continueWithOutputsAsync, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ProcessMaybeInputsOwnedAsync(PayjoinReceiverStateContext context, MaybeInputsOwned proposal, Func<WantsOutputs, PayjoinReceiverStateContext, CancellationToken, Task> continueWithOutputsAsync, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ProcessMaybeInputsSeenAsync(PayjoinReceiverStateContext context, MaybeInputsSeen proposal, Func<WantsOutputs, PayjoinReceiverStateContext, CancellationToken, Task> continueWithOutputsAsync, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ProcessOutputsUnknownAsync(PayjoinReceiverStateContext context, OutputsUnknown proposal, Func<WantsOutputs, PayjoinReceiverStateContext, CancellationToken, Task> continueWithOutputsAsync, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoOpInvoiceLookup : IPayjoinInvoiceLookup
    {
        public Task<InvoiceEntity?> GetInvoiceAsync(string invoiceId) => Task.FromResult<InvoiceEntity?>(null);
    }

    private sealed class NoOpOutputBuilder : IPayjoinReceiverOutputBuilder
    {
        public Task<PayjoinReceiverOutputBuilder.OutputReplacement?> TryCreateSettlementOutputsAsync(string storeId, string invoiceId, byte[] receiverScript, bool preserveReceiverScript, CancellationToken cancellationToken) => Task.FromResult<PayjoinReceiverOutputBuilder.OutputReplacement?>(null);
    }

    private sealed class NoOpInputSelector : IPayjoinReceiverInputSelector
    {
        public Task<ReceiverInputContributionResult> TryContributeInputsAsync(WantsInputs proposal, string storeId, string invoiceId, DateTimeOffset reservationExpiresAt, CancellationToken cancellationToken) => Task.FromResult(ReceiverInputContributionResult.Failure("not used"));
        public Task<ReceivedCoin[]?> TryGetPersistedContributedCoinsAsync(PayjoinReceiverSessionState session, CancellationToken cancellationToken) => Task.FromResult<ReceivedCoin[]?>(null);
    }

    private sealed class NoOpProposalFinalizer : IPayjoinReceiverProposalFinalizer
    {
        public Task FinalizeAsync(PayjoinReceiverProposalFinalizationContext context, WantsFeeRange proposal, ReceivedCoin[] contributedCoins, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task FinalizeAsync(PayjoinReceiverProposalFinalizationContext context, ProvisionalProposal proposal, ReceivedCoin[] contributedCoins, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PostAsync(PayjoinReceiverProposalFinalizationContext context, PayjoinProposal proposal, CancellationToken cancellationToken) => Task.CompletedTask;
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
        public Task<PaymentEntity?> ReconcileWithFinalTransactionAsync(PayjoinAccountingBridgeState bridge, CancellationToken cancellationToken) => Task.FromResult<PaymentEntity?>(null);
    }

    private sealed class SelectiveGuard(string failingInvoiceId) : IPayjoinReceiverSessionGuard
    {
        public ConcurrentBag<string> VisitedInvoiceIds { get; } = [];

        public Task<PayjoinReceiverSessionGuardResult?> TryPrepareAsync(PayjoinReceiverSessionState session, CancellationToken cancellationToken)
        {
            VisitedInvoiceIds.Add(session.InvoiceId);
            if (string.Equals(session.InvoiceId, failingInvoiceId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Simulated invalid receiver state.");
            }

            return Task.FromResult<PayjoinReceiverSessionGuardResult?>(null);
        }
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
            var databaseName = $"payjoin-session-processor-tests-{Guid.NewGuid():N}";
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
