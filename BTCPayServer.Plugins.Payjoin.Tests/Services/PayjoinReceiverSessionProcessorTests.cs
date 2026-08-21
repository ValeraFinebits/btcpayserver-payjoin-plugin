using BTCPayServer.Logging;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Wallets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NBXplorer;
using System.Collections.Concurrent;
using Xunit;
using HasReplyableError = Payjoin.HasReplyableError;
using Initialized = Payjoin.Initialized;
using IPayjoinProposal = Payjoin.IPayjoinProposal;
using MaybeInputsOwned = Payjoin.MaybeInputsOwned;
using MaybeInputsSeen = Payjoin.MaybeInputsSeen;
using OutputsUnknown = Payjoin.OutputsUnknown;
using ProvisionalProposal = Payjoin.ProvisionalProposal;
using ReceiveSession = Payjoin.ReceiveSession;
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
        using var testContext = new SessionStoreFixture();
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
        using var testContext = new SessionStoreFixture();
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

    [Fact]
    public async Task ProcessTickAsyncPreservesSessionAfterTransientReceiverPersistenceFailure()
    {
        // Arrange
        using var testContext = new SessionStoreFixture();
        var store = testContext.CreateStore();
        var session = CreateSession(store, "invoice-transient-persistence-failure");
        var processor = CreateProcessor(store, new TransientPersistenceFailureGuard());

        // Act
        await processor.ProcessTickAsync(CancellationToken.None);

        // Assert
        Assert.True(store.TryGetSession(session.InvoiceId, out var reloadedSession));
        Assert.NotNull(reloadedSession);
    }

    [Fact]
    public async Task ProcessTickAsyncRetainsFatalSessionUntilReplayShowsClosed()
    {
        // Arrange
        using var testContext = new SessionStoreFixture();
        var store = testContext.CreateStore();
        var session = CreateSession(store, "invoice-fatal-persistence-failure");
        var guard = new FatalThenClosedReplayGuard();
        var processor = CreateProcessor(store, guard);

        // Act
        await processor.ProcessTickAsync(CancellationToken.None);

        // Assert
        Assert.IsType<global::Payjoin.ReceiverPersistedException.Fatal>(guard.Failure);
        Assert.True(store.TryGetSession(session.InvoiceId, out var retainedSession));
        Assert.NotNull(retainedSession);

        // Act
        await processor.ProcessTickAsync(CancellationToken.None);

        // Assert
        Assert.Equal(2, guard.Attempts);
        Assert.False(store.TryGetSession(session.InvoiceId, out _));
    }

    [Fact]
    public async Task ProcessTickAsyncPreservesSessionAndRetriesAfterStorageReceiverPersistenceFailure()
    {
        // Arrange
        using var testContext = new SessionStoreFixture();
        var store = testContext.CreateStore();
        var session = CreateSession(store, "invoice-storage-persistence-failure");
        var guard = new StorageThenSuccessGuard();
        var processor = CreateProcessor(store, guard);

        // Act
        await processor.ProcessTickAsync(CancellationToken.None);

        Assert.IsType<global::Payjoin.ReceiverPersistedException.Storage>(guard.FirstFailure);

        // Assert
        Assert.True(store.TryGetSession(session.InvoiceId, out var retainedSession));
        Assert.NotNull(retainedSession);

        // Act
        await processor.ProcessTickAsync(CancellationToken.None);

        // Assert
        Assert.Equal(2, guard.Attempts);
        Assert.True(store.TryGetSession(session.InvoiceId, out var retriedSession));
        Assert.NotNull(retriedSession);
    }

    [Fact]
    public async Task ProcessTickAsyncIsolatesDatabaseConcurrencyFailuresPerSession()
    {
        using var testContext = new SessionStoreFixture();
        var store = testContext.CreateStore();
        var failingSession = CreateSession(store, "invoice-db-conflict");
        var survivingSession = CreateSession(store, "invoice-db-surviving");
        var guard = new DbConflictGuard(failingSession.InvoiceId);
        var processor = CreateProcessor(store, guard);

        await processor.ProcessTickAsync(CancellationToken.None);

        Assert.Contains(failingSession.InvoiceId, guard.VisitedInvoiceIds);
        Assert.Contains(survivingSession.InvoiceId, guard.VisitedInvoiceIds);
        Assert.True(store.TryGetSession(failingSession.InvoiceId, out _));
        Assert.True(store.TryGetSession(survivingSession.InvoiceId, out _));
    }

    [Fact]
    public async Task ProcessTickAsyncRecordsTheExpectedFinalTransactionBeforeRepostingAReplayedProposal()
    {
        // Arrange: the session replays straight to the PayjoinProposal state, as it does when a
        // previous run stopped between finalizing the proposal and completing the bridge write.
        using var testContext = new SessionStoreFixture();
        var store = testContext.CreateStore();
        CreateSession(store, "invoice-replayed-proposal");
        var guard = new ReplayedStateGuard(() => new ReceiveSession.PayjoinProposal(null!));
        var finalizer = new RecordingProposalFinalizer();
        var processor = CreateProcessor(store, guard, finalizer);

        // Act
        await processor.ProcessTickAsync(CancellationToken.None);

        // Assert
        Assert.Equal(
            new[] { nameof(IPayjoinReceiverProposalFinalizer.EnsureExpectedFinalTransactionAsync), nameof(IPayjoinReceiverProposalFinalizer.PostAsync) },
            finalizer.Calls);
    }

    [Fact]
    public async Task ProcessTickAsyncDispatchesReplayedPendingFallbackForClosure()
    {
        // Arrange
        using var testContext = new SessionStoreFixture();
        var store = testContext.CreateStore();
        var session = CreateSession(store, "invoice-replayed-pending-fallback");
        var guard = new ReplayedStateGuard(() => new ReceiveSession.ReceiverPendingFallback(null!));
        var stateProcessor = new RecordingPendingFallbackStateProcessor();
        var processor = CreateProcessor(store, guard, stateProcessor: stateProcessor);

        // Act
        await processor.ProcessTickAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1, stateProcessor.PendingFallbackCalls);
        Assert.True(store.TryGetSession(session.InvoiceId, out _));
    }

    private static PayjoinReceiverSessionProcessor CreateProcessor(
        PayjoinReceiverSessionStore sessionStore,
        IPayjoinReceiverSessionGuard sessionGuard,
        IPayjoinReceiverProposalFinalizer? proposalFinalizer = null,
        IPayjoinReceiverStateProcessor? stateProcessor = null)
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
            stateProcessor ?? new NoOpStateProcessor(),
            new NoOpOutputBuilder(),
            new NoOpInputSelector(),
            new NoOpAccountingBridgeService(),
            new NoOpAccountingPaymentService(),
            new NoOpInvoiceLookup(),
            proposalFinalizer ?? new NoOpProposalFinalizer(),
            networkProvider,
            NullLogger<PayjoinReceiverSessionProcessor>.Instance);
    }

    private static PayjoinReceiverSessionState CreateSession(PayjoinReceiverSessionStore store, string invoiceId)
    {
        return store.GetOrCreateSession(
            invoiceId,
            "bcrt1qexampleaddress0000000000000000000000000",
            "store-1",
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
        public Task ProcessPendingFallbackAsync(PayjoinReceiverStateContext context, global::Payjoin.ReceiverPendingFallback pendingFallback, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ProcessUncheckedProposalAsync(PayjoinReceiverStateContext context, UncheckedOriginalPayload proposal, Func<WantsOutputs, PayjoinReceiverStateContext, CancellationToken, Task> continueWithOutputsAsync, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ProcessMaybeInputsOwnedAsync(PayjoinReceiverStateContext context, MaybeInputsOwned proposal, Func<WantsOutputs, PayjoinReceiverStateContext, CancellationToken, Task> continueWithOutputsAsync, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ProcessMaybeInputsSeenAsync(PayjoinReceiverStateContext context, MaybeInputsSeen proposal, Func<WantsOutputs, PayjoinReceiverStateContext, CancellationToken, Task> continueWithOutputsAsync, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ProcessOutputsUnknownAsync(PayjoinReceiverStateContext context, OutputsUnknown proposal, Func<WantsOutputs, PayjoinReceiverStateContext, CancellationToken, Task> continueWithOutputsAsync, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingPendingFallbackStateProcessor : IPayjoinReceiverStateProcessor
    {
        public int PendingFallbackCalls { get; private set; }

        public Task ProcessInitializedAsync(PayjoinReceiverStateContext context, Initialized initialized, Func<WantsOutputs, PayjoinReceiverStateContext, CancellationToken, Task> continueWithOutputsAsync, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ProcessReplyableErrorAsync(PayjoinReceiverStateContext context, HasReplyableError replyableError, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ProcessUncheckedProposalAsync(PayjoinReceiverStateContext context, UncheckedOriginalPayload proposal, Func<WantsOutputs, PayjoinReceiverStateContext, CancellationToken, Task> continueWithOutputsAsync, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ProcessMaybeInputsOwnedAsync(PayjoinReceiverStateContext context, MaybeInputsOwned proposal, Func<WantsOutputs, PayjoinReceiverStateContext, CancellationToken, Task> continueWithOutputsAsync, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ProcessMaybeInputsSeenAsync(PayjoinReceiverStateContext context, MaybeInputsSeen proposal, Func<WantsOutputs, PayjoinReceiverStateContext, CancellationToken, Task> continueWithOutputsAsync, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ProcessOutputsUnknownAsync(PayjoinReceiverStateContext context, OutputsUnknown proposal, Func<WantsOutputs, PayjoinReceiverStateContext, CancellationToken, Task> continueWithOutputsAsync, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ProcessPendingFallbackAsync(
            PayjoinReceiverStateContext context,
            global::Payjoin.ReceiverPendingFallback pendingFallback,
            CancellationToken cancellationToken)
        {
            PendingFallbackCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpInvoiceLookup : IPayjoinInvoiceLookup
    {
        public Task<InvoiceEntity?> GetInvoiceAsync(string invoiceId) => Task.FromResult<InvoiceEntity?>(null);
    }

    private sealed class NoOpOutputBuilder : IPayjoinReceiverOutputBuilder
    {
        public Task<PayjoinReceiverOutputBuilder.OutputReplacement?> TryCreateSettlementOutputsAsync(string storeId, string invoiceId, byte[] receiverScript, bool preserveReceiverScript, long? pinnedSettlementAmountSats, CancellationToken cancellationToken) => Task.FromResult<PayjoinReceiverOutputBuilder.OutputReplacement?>(null);
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
        public Task EnsureExpectedFinalTransactionAsync(PayjoinReceiverProposalFinalizationContext context, IPayjoinProposal proposal, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PostAsync(PayjoinReceiverProposalFinalizationContext context, IPayjoinProposal proposal, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingProposalFinalizer : IPayjoinReceiverProposalFinalizer
    {
        public List<string> Calls { get; } = [];

        public Task FinalizeAsync(PayjoinReceiverProposalFinalizationContext context, WantsFeeRange proposal, ReceivedCoin[] contributedCoins, CancellationToken cancellationToken)
        {
            Calls.Add(nameof(FinalizeAsync));
            return Task.CompletedTask;
        }

        public Task FinalizeAsync(PayjoinReceiverProposalFinalizationContext context, ProvisionalProposal proposal, ReceivedCoin[] contributedCoins, CancellationToken cancellationToken)
        {
            Calls.Add(nameof(FinalizeAsync));
            return Task.CompletedTask;
        }

        public Task EnsureExpectedFinalTransactionAsync(PayjoinReceiverProposalFinalizationContext context, IPayjoinProposal proposal, CancellationToken cancellationToken)
        {
            Calls.Add(nameof(EnsureExpectedFinalTransactionAsync));
            return Task.CompletedTask;
        }

        public Task PostAsync(PayjoinReceiverProposalFinalizationContext context, IPayjoinProposal proposal, CancellationToken cancellationToken)
        {
            Calls.Add(nameof(PostAsync));
            return Task.CompletedTask;
        }
    }

    private sealed class ReplayedStateGuard(Func<ReceiveSession> createState) : IPayjoinReceiverSessionGuard
    {
        public Task<PayjoinReceiverSessionGuardResult?> TryPrepareAsync(PayjoinReceiverSessionState session, CancellationToken cancellationToken)
        {
            return Task.FromResult<PayjoinReceiverSessionGuardResult?>(new PayjoinReceiverSessionGuardResult(
                session,
                persister: null!,
                receiverScript: [0x00, 0x14],
                replay: null!,
                state: createState(),
                removeCloseRequestedSession: _ => false));
        }
    }

    private sealed class NoOpAccountingBridgeService : IPayjoinAccountingBridgeService
    {
        public Task<PayjoinAccountingBridgeState> CreateOrGetAsync(CreatePayjoinAccountingBridgeRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PayjoinAccountingBridgeState?> TryGetByInvoiceIdAsync(string invoiceId, CancellationToken cancellationToken) => Task.FromResult<PayjoinAccountingBridgeState?>(null);
        public Task<IReadOnlyCollection<PayjoinAccountingBridgeState>> GetPendingAsync(DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyCollection<PayjoinAccountingBridgeState>>([]);
        public Task<PayjoinAccountingBridgeState?> AttachFallbackAsync(string invoiceId, string fallbackTransactionId, long fallbackOutputIndex, long fallbackValueSats, long effectiveInvoiceValueSats, string? settlementScript, CancellationToken cancellationToken) => Task.FromResult<PayjoinAccountingBridgeState?>(null);
        public Task<PayjoinAccountingBridgeState?> SetExpectedFinalTransactionAsync(string invoiceId, string expectedFinalTransactionId, long? expectedFinalOutputIndex, long? expectedFinalValueSats, CancellationToken cancellationToken) => Task.FromResult<PayjoinAccountingBridgeState?>(null);
        public Task<PayjoinAccountingBridgeState?> MarkReconciledAsync(string invoiceId, string? expectedFinalTransactionId, long? expectedFinalOutputIndex, long? expectedFinalValueSats, DateTimeOffset reconciledAt, CancellationToken cancellationToken) => Task.FromResult<PayjoinAccountingBridgeState?>(null);
        public Task<PayjoinAccountingBridgeState?> MarkFailedAsync(string invoiceId, string failureMessage, CancellationToken cancellationToken) => Task.FromResult<PayjoinAccountingBridgeState?>(null);
        public Task<IReadOnlyCollection<PayjoinAccountingBridgeState>> ExpirePendingAsync(DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyCollection<PayjoinAccountingBridgeState>>([]);
        public Task<PayjoinAccountingBridgeAttentionResult> GetRequiringAttentionAsync(string storeId, CancellationToken cancellationToken) => Task.FromResult(new PayjoinAccountingBridgeAttentionResult([], 0));
        public Task<PayjoinAccountingBridgeState?> TryRetryAsync(string invoiceId, string storeId, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult<PayjoinAccountingBridgeState?>(null);
        public Task<PayjoinAccountingBridgeState?> ResetForNewSessionAsync(string invoiceId, long? effectiveInvoiceValueSats, DateTimeOffset? expiresAt, CancellationToken cancellationToken) => Task.FromResult<PayjoinAccountingBridgeState?>(null);
    }

    private sealed class NoOpAccountingPaymentService : IPayjoinAccountingPaymentService
    {
        public Task<PaymentEntity?> ReconcileWithFinalTransactionAsync(PayjoinAccountingBridgeState bridge, CancellationToken cancellationToken) => Task.FromResult<PaymentEntity?>(null);
    }

    private sealed class DbConflictGuard(string failingInvoiceId) : IPayjoinReceiverSessionGuard
    {
        public ConcurrentBag<string> VisitedInvoiceIds { get; } = [];

        public Task<PayjoinReceiverSessionGuardResult?> TryPrepareAsync(PayjoinReceiverSessionState session, CancellationToken cancellationToken)
        {
            VisitedInvoiceIds.Add(session.InvoiceId);
            if (string.Equals(session.InvoiceId, failingInvoiceId, StringComparison.Ordinal))
            {
                throw new DbUpdateConcurrencyException("Simulated concurrent session write.");
            }

            return Task.FromResult<PayjoinReceiverSessionGuardResult?>(null);
        }
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
    private sealed class TransientPersistenceFailureGuard : IPayjoinReceiverSessionGuard
    {
        public Task<PayjoinReceiverSessionGuardResult?> TryPrepareAsync(PayjoinReceiverSessionState session, CancellationToken cancellationToken)
        {
            throw new global::Payjoin.ReceiverPersistedException.Transient(new global::Payjoin.ReceiverException.Unexpected());
        }
    }

    private sealed class FatalThenClosedReplayGuard : IPayjoinReceiverSessionGuard
    {
        private const int EncapsulatedMessageBytes = 8192;
        private const string OhttpKeysHex =
            "01001604ba48c49c3d4a92a3ad00ecc63a024da10ced02180c73ec12d8a7ad2cc91bb483824fe2bee8d28bfe2eb2fc6453bc4d31cd851e8a6540e86c5382af588d370957000400010003";
        private readonly InMemoryReceiverPersister _persister = new();

        public Exception? Failure { get; private set; }

        public int Attempts { get; private set; }

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Reliability",
            "CA2000:Dispose objects before losing scope",
            Justification = "PayjoinReceiverSessionGuardResult takes ownership of the replayed state and disposes it.")]
        public Task<PayjoinReceiverSessionGuardResult?> TryPrepareAsync(PayjoinReceiverSessionState session, CancellationToken cancellationToken)
        {
            Attempts++;
            if (Attempts > 1)
            {
                var replay = global::Payjoin.PayjoinMethods.ReplayReceiverEventLog(_persister);
                var replayedState = replay.State();
                return Task.FromResult<PayjoinReceiverSessionGuardResult?>(new PayjoinReceiverSessionGuardResult(
                    session,
                    _persister,
                    receiverScript: [0x00, 0x14],
                    replay,
                    replayedState,
                    removeCloseRequestedSession: _ => false));
            }

            using var ohttpKeys = global::Payjoin.OhttpKeys.Decode(Convert.FromHexString(OhttpKeysHex));
            using var builder = new global::Payjoin.ReceiverBuilder(
                "tb1q6d3a2w975yny0asuvd9a67ner4nks58ff0q8g4",
                "https://example.com",
                ohttpKeys);
            using var initialTransition = builder.Build();
            using var initialized = initialTransition.Save(_persister);
            using var pollRequest = initialized.CreatePollRequest("https://example.com");
            using var fatalTransition = initialized.ProcessResponse(
                new byte[EncapsulatedMessageBytes],
                pollRequest.ClientResponse);

            try
            {
                using var unexpected = fatalTransition.Save(_persister);
                throw new InvalidOperationException("The malformed OHTTP response unexpectedly produced a receiver state.");
            }
            catch (Exception ex)
            {
                Failure = ex;
                throw;
            }
        }
    }

    private sealed class StorageThenSuccessGuard : IPayjoinReceiverSessionGuard
    {
        private const string OhttpKeysHex =
            "01001604ba48c49c3d4a92a3ad00ecc63a024da10ced02180c73ec12d8a7ad2cc91bb483824fe2bee8d28bfe2eb2fc6453bc4d31cd851e8a6540e86c5382af588d370957000400010003";

        public int Attempts { get; private set; }

        public Exception? FirstFailure { get; private set; }

        public Task<PayjoinReceiverSessionGuardResult?> TryPrepareAsync(PayjoinReceiverSessionState session, CancellationToken cancellationToken)
        {
            Attempts++;
            if (Attempts > 1)
            {
                return Task.FromResult<PayjoinReceiverSessionGuardResult?>(null);
            }

            using var ohttpKeys = global::Payjoin.OhttpKeys.Decode(Convert.FromHexString(OhttpKeysHex));
            using var builder = new global::Payjoin.ReceiverBuilder(
                "tb1q6d3a2w975yny0asuvd9a67ner4nks58ff0q8g4",
                "https://example.com",
                ohttpKeys);
            using var transition = builder.Build();
            using var initialized = transition.Save(new InMemoryReceiverPersister());
            using var cancelTransition = initialized.Cancel();
            try
            {
                using var unexpected = cancelTransition.Save(new ThrowingReceiverPersister());
                throw new InvalidOperationException("The failing persister unexpectedly saved the receiver transition.");
            }
            catch (Exception ex)
            {
                FirstFailure = ex;
                throw;
            }
        }
    }

    private sealed class InMemoryReceiverPersister : global::Payjoin.JsonReceiverSessionPersister
    {
        private readonly List<string> _events = [];

        public void Save(string @event) => _events.Add(@event);

        public string[] Load() => _events.ToArray();

        public void Close()
        {
        }
    }

    private sealed class ThrowingReceiverPersister : global::Payjoin.JsonReceiverSessionPersister
    {
        public void Save(string @event) => throw new InvalidOperationException("Simulated receiver storage failure.");

        public string[] Load() => [];

        public void Close()
        {
        }
    }

}
