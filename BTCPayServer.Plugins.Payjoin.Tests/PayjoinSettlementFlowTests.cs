using BTCPayServer.Data;
using BTCPayServer.Logging;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Wallets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NBXplorer;
using Xunit;
using CancelTransition = Payjoin.CancelTransition;
using ClientResponse = Payjoin.ClientResponse;
using IPayjoinProposal = Payjoin.IPayjoinProposal;
using PayjoinOutPoint = Payjoin.OutPoint;
using PayjoinProposalTransition = Payjoin.PayjoinProposalTransition;
using PayjoinTxOut = Payjoin.TxOut;
using ProcessPsbt = Payjoin.ProcessPsbt;
using RequestResponse = Payjoin.RequestResponse;
using Initialized = Payjoin.Initialized;
using WantsOutputs = Payjoin.WantsOutputs;
using WantsInputs = Payjoin.WantsInputs;
using WantsFeeRange = Payjoin.WantsFeeRange;
using ProvisionalProposal = Payjoin.ProvisionalProposal;
using HasReplyableError = Payjoin.HasReplyableError;
using UncheckedOriginalPayload = Payjoin.UncheckedOriginalPayload;
using MaybeInputsOwned = Payjoin.MaybeInputsOwned;
using MaybeInputsSeen = Payjoin.MaybeInputsSeen;
using OutputsUnknown = Payjoin.OutputsUnknown;

namespace BTCPayServer.Plugins.Payjoin.Tests;

/// <summary>
/// Flow-level tests that drive the real session store, accounting bridge service and poller across
/// one relational database, so the settlement invariants hold end-to-end rather than per helper.
/// </summary>
public class PayjoinSettlementFlowTests
{
    private const string InvoiceId = "invoice-flow";
    private const string StoreId = "store-1";

    [Fact]
    public void CommittingReceiverOutputsPersistsTheEventScriptAndPinnedAmountTogether()
    {
        using var testContext = new RelationalPluginTestContext();
        var store = testContext.CreateStore();
        var bridgeService = testContext.CreateBridgeService();
        CreateSession(store);
        CreateBridge(bridgeService);
        var processor = CreateProcessor(store, bridgeService);
        using var settlementKey = new Key();
        var settlementScript = settlementKey.PubKey.WitHash.ScriptPubKey.ToBytes();

        processor.PersistCommittedOutputs(
            InvoiceId,
            ["commit-outputs-event"],
            new PayjoinReceiverOutputBuilder.OutputReplacement([new PayjoinTxOut(1234, settlementScript)], settlementScript, 1234));

        using var context = testContext.CreateDbContext();
        var events = ReadEvents(context);
        Assert.Equal(new[] { "bootstrap-event", "commit-outputs-event" }, events);
        var bridge = Assert.Single(context.AccountingBridges.Where(x => x.InvoiceId == InvoiceId));
        Assert.Equal(Convert.ToHexString(settlementScript), bridge.SettlementScript);
        Assert.Equal(1234, bridge.EffectiveInvoiceValueSats);
    }

    [Fact]
    public void CommittingReceiverOutputsLeavesNothingBehindWhenPersistenceFails()
    {
        using var testContext = new RelationalPluginTestContext();
        var store = testContext.CreateStore();
        var bridgeService = testContext.CreateBridgeService();
        CreateSession(store);
        CreateBridge(bridgeService);
        var processor = CreateProcessor(store, bridgeService);
        using var settlementKey = new Key();
        var settlementScript = settlementKey.PubKey.WitHash.ScriptPubKey.ToBytes();

        testContext.FailSaveChanges = true;
        Assert.Throws<DbUpdateException>(() => processor.PersistCommittedOutputs(
            InvoiceId,
            ["commit-outputs-event"],
            new PayjoinReceiverOutputBuilder.OutputReplacement([new PayjoinTxOut(1234, settlementScript)], settlementScript, 1234)));
        testContext.FailSaveChanges = false;

        using var context = testContext.CreateDbContext();
        Assert.Equal(new[] { "bootstrap-event" }, ReadEvents(context));
        var bridge = Assert.Single(context.AccountingBridges.Where(x => x.InvoiceId == InvoiceId));
        Assert.Null(bridge.SettlementScript);
        Assert.Null(bridge.EffectiveInvoiceValueSats);
    }

    [Fact]
    public async Task FinalizingPersistsTheExpectedFinalTransactionBeforeThePostAttempt()
    {
        using var testContext = new RelationalPluginTestContext();
        var store = testContext.CreateStore();
        var bridgeService = testContext.CreateBridgeService();
        CreateSession(store);
        var proposal = CreateProposal(out var finalTransaction, out var settlementScript);
        CreateBridge(bridgeService, settlementScriptHex: Convert.ToHexString(settlementScript.ToBytes()));
        var relaySender = new PostAttemptRecordingRelaySender();
        var finalizer = CreateFinalizer(store, bridgeService, relaySender);

        await Assert.ThrowsAsync<PostAttemptedException>(() => finalizer.FinalizeCoreAsync(
            CreateFinalizationContext(),
            persister =>
            {
                persister.Save("finalize-event");
                return proposal;
            },
            CancellationToken.None));

        // The relay post was attempted, and everything the accounting side needs to reconcile the
        // final transaction was already durable at that point.
        Assert.Equal(1, relaySender.PostAttempts);
        using var context = testContext.CreateDbContext();
        Assert.Equal(new[] { "bootstrap-event", "finalize-event" }, ReadEvents(context));
        var bridge = Assert.Single(context.AccountingBridges.Where(x => x.InvoiceId == InvoiceId));
        Assert.Equal(finalTransaction.GetHash().ToString(), bridge.ExpectedFinalTransactionId);
        Assert.Equal(1, bridge.ExpectedFinalOutputIndex);
        Assert.Equal(20_000, bridge.ExpectedFinalValueSats);
        Assert.Equal(Data.PayjoinAccountingBridgeStatus.PendingFinalTransaction, bridge.Status);
    }

    [Fact]
    public async Task FinalizingDoesNotPostWhenPersistenceFails()
    {
        using var testContext = new RelationalPluginTestContext();
        var store = testContext.CreateStore();
        var bridgeService = testContext.CreateBridgeService();
        CreateSession(store);
        var proposal = CreateProposal(out _, out var settlementScript);
        CreateBridge(bridgeService, settlementScriptHex: Convert.ToHexString(settlementScript.ToBytes()));
        var relaySender = new PostAttemptRecordingRelaySender();
        var finalizer = CreateFinalizer(store, bridgeService, relaySender);

        testContext.FailSaveChanges = true;
        await Assert.ThrowsAsync<DbUpdateException>(() => finalizer.FinalizeCoreAsync(
            CreateFinalizationContext(),
            persister =>
            {
                persister.Save("finalize-event");
                return proposal;
            },
            CancellationToken.None));
        testContext.FailSaveChanges = false;

        Assert.Equal(0, relaySender.PostAttempts);
        using var context = testContext.CreateDbContext();
        Assert.Equal(new[] { "bootstrap-event" }, ReadEvents(context));
        var bridge = Assert.Single(context.AccountingBridges.Where(x => x.InvoiceId == InvoiceId));
        Assert.Null(bridge.ExpectedFinalTransactionId);
        Assert.Equal(Data.PayjoinAccountingBridgeStatus.PendingFallback, bridge.Status);
    }

    [Fact]
    public async Task ReplayAfterAPersistedFinalizationAppendsNoDuplicateEventsOrBridgeWrites()
    {
        using var testContext = new RelationalPluginTestContext();
        var store = testContext.CreateStore();
        var bridgeService = testContext.CreateBridgeService();
        CreateSession(store);
        var proposal = CreateProposal(out _, out var settlementScript);
        CreateBridge(bridgeService, settlementScriptHex: Convert.ToHexString(settlementScript.ToBytes()));
        var relaySender = new PostAttemptRecordingRelaySender();
        var finalizer = CreateFinalizer(store, bridgeService, relaySender);
        await Assert.ThrowsAsync<PostAttemptedException>(() => finalizer.FinalizeCoreAsync(
            CreateFinalizationContext(),
            persister =>
            {
                persister.Save("finalize-event");
                return proposal;
            },
            CancellationToken.None));
        var bridgeAfterFinalize = await bridgeService.TryGetByInvoiceIdAsync(InvoiceId, CancellationToken.None);

        // The replay path re-records the expectation and re-posts; with the finalization already
        // persisted it must observe the bridge up to date and change nothing.
        await finalizer.EnsureExpectedFinalTransactionAsync(CreateFinalizationContext(), proposal, CancellationToken.None);
        await Assert.ThrowsAsync<PostAttemptedException>(() => finalizer.PostAsync(CreateFinalizationContext(), proposal, CancellationToken.None));

        var bridgeAfterReplay = await bridgeService.TryGetByInvoiceIdAsync(InvoiceId, CancellationToken.None);
        Assert.Equal(bridgeAfterFinalize, bridgeAfterReplay);
        using var context = testContext.CreateDbContext();
        Assert.Equal(new[] { "bootstrap-event", "finalize-event" }, ReadEvents(context));
    }

    [Fact]
    public async Task ReconciliationMismatchMarksTheBridgeFailedWithoutCreditingAPayment()
    {
        using var testContext = new RelationalPluginTestContext();
        var store = testContext.CreateStore();
        var bridgeService = testContext.CreateBridgeService();
        var paymentService = new ObservedValuePaymentService(observedValueSats: 999);
        CreateBridge(
            bridgeService,
            expectedFinalTransactionId: "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            expectedFinalValueSats: 1234);
        using var poller = CreatePoller(store, bridgeService, paymentService);

        await poller.ProcessTickOnceAsync(CancellationToken.None);

        Assert.Equal(0, paymentService.CreditedPayments);
        var bridge = await bridgeService.TryGetByInvoiceIdAsync(InvoiceId, CancellationToken.None);
        Assert.NotNull(bridge);
        Assert.Equal(Data.PayjoinAccountingBridgeStatus.Failed, bridge!.Status);
        Assert.Contains("does not match", bridge.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReconciliationCreditsThePinnedAmountWhenTheObservedValueMatches()
    {
        using var testContext = new RelationalPluginTestContext();
        var store = testContext.CreateStore();
        var bridgeService = testContext.CreateBridgeService();
        var paymentService = new ObservedValuePaymentService(observedValueSats: 1234);
        CreateBridge(
            bridgeService,
            expectedFinalTransactionId: "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            expectedFinalValueSats: 1234);
        using var poller = CreatePoller(store, bridgeService, paymentService);

        await poller.ProcessTickOnceAsync(CancellationToken.None);

        Assert.Equal(1, paymentService.CreditedPayments);
        var bridge = await bridgeService.TryGetByInvoiceIdAsync(InvoiceId, CancellationToken.None);
        Assert.NotNull(bridge);
        Assert.Equal(Data.PayjoinAccountingBridgeStatus.Reconciled, bridge!.Status);
    }

    private static void CreateSession(PayjoinReceiverSessionStore store)
    {
        store.GetOrCreateSession(
            InvoiceId,
            "bcrt1qexampleaddress0000000000000000000000000",
            StoreId,
            DateTimeOffset.UtcNow.AddMinutes(15),
            ["bootstrap-event"]);
    }

    private static void CreateBridge(
        PayjoinAccountingBridgeService bridgeService,
        string? settlementScriptHex = null,
        string? expectedFinalTransactionId = null,
        long? expectedFinalValueSats = null)
    {
        bridgeService.CreateOrGetAsync(
            new CreatePayjoinAccountingBridgeRequest(
                InvoiceId,
                StoreId,
                PayjoinConstants.BitcoinCode,
                "BTC-BTC",
                DateTimeOffset.UtcNow.AddHours(1),
                SettlementScript: settlementScriptHex,
                ExpectedFinalTransactionId: expectedFinalTransactionId,
                ExpectedFinalOutputIndex: expectedFinalTransactionId is null ? null : 1,
                ExpectedFinalValueSats: expectedFinalValueSats),
            CancellationToken.None).GetAwaiter().GetResult();
    }

    private static string[] ReadEvents(PayjoinPluginDbContext context)
    {
        return context.ReceiverSessionEvents
            .Where(x => x.InvoiceId == InvoiceId)
            .OrderBy(x => x.Sequence)
            .Select(x => x.Event)
            .ToArray();
    }

    private static PayjoinReceiverProposalFinalizationContext CreateFinalizationContext()
    {
        return new PayjoinReceiverProposalFinalizationContext(
            persister: null!,
            StoreId,
            InvoiceId,
            PayjoinConstants.BitcoinCode);
    }

    private static PayjoinReceiverProposalFinalizer CreateFinalizer(
        PayjoinReceiverSessionStore store,
        PayjoinAccountingBridgeService bridgeService,
        IPayjoinReceiverRelayRequestSender relaySender)
    {
        return new PayjoinReceiverProposalFinalizer(
            relaySender,
            new UnusedProposalSigner(),
            bridgeService,
            store,
            CreateNetworkProvider());
    }

    private static PayjoinReceiverSessionProcessor CreateProcessor(
        PayjoinReceiverSessionStore store,
        PayjoinAccountingBridgeService bridgeService)
    {
        return new PayjoinReceiverSessionProcessor(
            store,
            new UnusedSessionGuard(),
            new UnusedStateProcessor(),
            new UnusedOutputBuilder(),
            new UnusedInputSelector(),
            bridgeService,
            new UnusedPaymentService(),
            new UnusedInvoiceLookup(),
            new UnusedProposalFinalizer(),
            CreateNetworkProvider(),
            NullLogger<PayjoinReceiverSessionProcessor>.Instance);
    }

    private static PayjoinReceiverPoller CreatePoller(
        PayjoinReceiverSessionStore store,
        PayjoinAccountingBridgeService bridgeService,
        IPayjoinAccountingPaymentService paymentService)
    {
        return new PayjoinReceiverPoller(
            store,
            new NoOpSessionProcessor(),
            bridgeService,
            paymentService,
            NullLogger<PayjoinReceiverPoller>.Instance);
    }

    private static FixedPsbtProposal CreateProposal(out Transaction finalTransaction, out Script settlementScript)
    {
        using var receiverKey = new Key();
        using var settlementKey = new Key();
        finalTransaction = Network.RegTest.CreateTransaction();
        finalTransaction.Inputs.Add(new OutPoint(uint256.Parse("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), 0));
        finalTransaction.Outputs.Add(Money.Satoshis(10_000), receiverKey.PubKey.WitHash.ScriptPubKey);
        settlementScript = settlementKey.PubKey.WitHash.ScriptPubKey;
        finalTransaction.Outputs.Add(Money.Satoshis(20_000), settlementScript);

        return new FixedPsbtProposal(PSBT.FromTransaction(finalTransaction, Network.RegTest).ToBase64());
    }

    private static BTCPayNetworkProvider CreateNetworkProvider()
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

        return new BTCPayNetworkProvider([network], nbxplorerNetworkProvider, new Logs());
    }

    private sealed class PostAttemptedException : Exception
    {
        public PostAttemptedException()
        {
        }

        public PostAttemptedException(string message) : base(message)
        {
        }

        public PostAttemptedException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

    private sealed class PostAttemptRecordingRelaySender : IPayjoinReceiverRelayRequestSender
    {
        public int PostAttempts { get; private set; }

        public Task<(byte[] ResponseBody, TRequestContext RequestContext)> SendAsync<TRequestContext>(
            string storeId,
            string invoiceId,
            Func<string, TRequestContext> buildRequest,
            Func<TRequestContext, (Uri Url, string ContentType, byte[] Body)> describeRequest,
            CancellationToken cancellationToken)
            where TRequestContext : IDisposable
        {
            PostAttempts++;
            throw new PostAttemptedException();
        }
    }

    /// <summary>
    /// Runs the real observed-value check against a canned on-chain observation and records whether
    /// the flow reached the crediting step.
    /// </summary>
    private sealed class ObservedValuePaymentService(long observedValueSats) : IPayjoinAccountingPaymentService
    {
        public int CreditedPayments { get; private set; }

        public Task<PaymentEntity?> ReconcileWithFinalTransactionAsync(PayjoinAccountingBridgeState bridge, CancellationToken cancellationToken)
        {
            PayjoinAccountingPaymentService.EnsureObservedSettlementValueMatchesExpected(bridge, observedValueSats);
            CreditedPayments++;
            return Task.FromResult<PaymentEntity?>(new PaymentEntity { Status = PaymentStatus.Settled });
        }
    }

    private sealed class FixedPsbtProposal(string psbt) : IPayjoinProposal
    {
        public CancelTransition Cancel() => throw new NotSupportedException();
        public RequestResponse CreatePostRequest(string ohttpRelay) => throw new NotSupportedException();
        public PayjoinProposalTransition ProcessResponse(byte[] body, ClientResponse ohttpContext) => throw new NotSupportedException();
        public string Psbt() => psbt;
        public bool ProposalTxidIsStable() => throw new NotSupportedException();
    }

    private sealed class NoOpSessionProcessor : IPayjoinReceiverSessionProcessor
    {
        public Task ProcessTickAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }

    private sealed class UnusedProposalSigner : IPayjoinReceiverProposalSigner
    {
        public Task<ProcessPsbt> CreateContributedInputSignerAsync(string storeId, ReceivedCoin[] receiverCoins, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class UnusedSessionGuard : IPayjoinReceiverSessionGuard
    {
        public Task<PayjoinReceiverSessionGuardResult?> TryPrepareAsync(PayjoinReceiverSessionState session, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class UnusedStateProcessor : IPayjoinReceiverStateProcessor
    {
        public Task ProcessInitializedAsync(PayjoinReceiverStateContext context, Initialized initialized, Func<WantsOutputs, PayjoinReceiverStateContext, CancellationToken, Task> continueWithOutputsAsync, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ProcessReplyableErrorAsync(PayjoinReceiverStateContext context, HasReplyableError replyableError, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ProcessUncheckedProposalAsync(PayjoinReceiverStateContext context, UncheckedOriginalPayload proposal, Func<WantsOutputs, PayjoinReceiverStateContext, CancellationToken, Task> continueWithOutputsAsync, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ProcessMaybeInputsOwnedAsync(PayjoinReceiverStateContext context, MaybeInputsOwned proposal, Func<WantsOutputs, PayjoinReceiverStateContext, CancellationToken, Task> continueWithOutputsAsync, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ProcessMaybeInputsSeenAsync(PayjoinReceiverStateContext context, MaybeInputsSeen proposal, Func<WantsOutputs, PayjoinReceiverStateContext, CancellationToken, Task> continueWithOutputsAsync, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ProcessOutputsUnknownAsync(PayjoinReceiverStateContext context, OutputsUnknown proposal, Func<WantsOutputs, PayjoinReceiverStateContext, CancellationToken, Task> continueWithOutputsAsync, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class UnusedOutputBuilder : IPayjoinReceiverOutputBuilder
    {
        public Task<PayjoinReceiverOutputBuilder.OutputReplacement?> TryCreateSettlementOutputsAsync(string storeId, string invoiceId, byte[] receiverScript, bool preserveReceiverScript, long? pinnedSettlementAmountSats, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class UnusedInputSelector : IPayjoinReceiverInputSelector
    {
        public Task<ReceiverInputContributionResult> TryContributeInputsAsync(WantsInputs proposal, string storeId, string invoiceId, DateTimeOffset reservationExpiresAt, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ReceivedCoin[]?> TryGetPersistedContributedCoinsAsync(PayjoinReceiverSessionState session, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class UnusedPaymentService : IPayjoinAccountingPaymentService
    {
        public Task<PaymentEntity?> ReconcileWithFinalTransactionAsync(PayjoinAccountingBridgeState bridge, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class UnusedInvoiceLookup : IPayjoinInvoiceLookup
    {
        public Task<InvoiceEntity?> GetInvoiceAsync(string invoiceId) => throw new NotSupportedException();
    }

    private sealed class UnusedProposalFinalizer : IPayjoinReceiverProposalFinalizer
    {
        public Task FinalizeAsync(PayjoinReceiverProposalFinalizationContext context, WantsFeeRange proposal, ReceivedCoin[] receiverCoins, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task FinalizeAsync(PayjoinReceiverProposalFinalizationContext context, ProvisionalProposal proposal, ReceivedCoin[] receiverCoins, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task EnsureExpectedFinalTransactionAsync(PayjoinReceiverProposalFinalizationContext context, IPayjoinProposal proposal, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task PostAsync(PayjoinReceiverProposalFinalizationContext context, IPayjoinProposal proposal, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
