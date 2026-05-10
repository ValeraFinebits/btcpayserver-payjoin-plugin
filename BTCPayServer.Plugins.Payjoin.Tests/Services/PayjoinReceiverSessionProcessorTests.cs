using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services.Wallets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;
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
    public async Task ProcessTickAsyncDoesNotOwnExpiredReservationCleanup()
    {
        // Arrange
        using var testContext = new TestContext();
        var store = testContext.CreateStore();
        var session = CreateSession(store, "invoice-expired-cleanup", out _);
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
        var failingSession = CreateSession(store, "invoice-failing", out _);
        var survivingSession = CreateSession(store, "invoice-surviving", out _);
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
        return new PayjoinReceiverSessionProcessor(
            sessionStore,
            sessionGuard,
            new NoOpStateProcessor(),
            new NoOpOutputBuilder(),
            new NoOpInputSelector(),
            new NoOpProposalFinalizer(),
            NullLogger<PayjoinReceiverSessionProcessor>.Instance);
    }

    private static PayjoinReceiverSessionState CreateSession(PayjoinReceiverSessionStore store, string invoiceId, out bool created)
    {
        return store.CreateSession(
            invoiceId,
            "bcrt1qexampleaddress0000000000000000000000000",
            "store-1",
            new global::System.Uri("https://relay.example/"),
            DateTimeOffset.UtcNow.AddMinutes(15),
            out created);
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

    private sealed class NoOpOutputBuilder : IPayjoinReceiverOutputBuilder
    {
        public Task<PayjoinReceiverOutputBuilder.OutputReplacement?> TryCreateExactPaymentOutputsAsync(string storeId, string invoiceId, byte[] receiverScript, CancellationToken cancellationToken) => Task.FromResult<PayjoinReceiverOutputBuilder.OutputReplacement?>(null);
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
