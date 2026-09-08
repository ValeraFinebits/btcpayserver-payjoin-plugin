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
using HasReplyableError = Payjoin.HasReplyableError;
using Initialized = Payjoin.Initialized;
using IPayjoinProposal = Payjoin.IPayjoinProposal;
using MaybeInputsOwned = Payjoin.MaybeInputsOwned;
using MaybeInputsSeen = Payjoin.MaybeInputsSeen;
using OutputsUnknown = Payjoin.OutputsUnknown;
using PayjoinProposalTransition = Payjoin.PayjoinProposalTransition;
using PayjoinTxOut = Payjoin.TxOut;
using ProcessPsbt = Payjoin.ProcessPsbt;
using ProvisionalProposal = Payjoin.ProvisionalProposal;
using RequestResponse = Payjoin.RequestResponse;
using UncheckedOriginalPayload = Payjoin.UncheckedOriginalPayload;
using WantsFeeRange = Payjoin.WantsFeeRange;
using WantsInputs = Payjoin.WantsInputs;
using WantsOutputs = Payjoin.WantsOutputs;

namespace BTCPayServer.Plugins.Payjoin.Tests;

/// <summary>
/// Flow-level tests that drive the real session store, accounting bridge service and poller across
/// one relational database, so the settlement invariants hold end-to-end rather than per helper.
/// </summary>
public class PayjoinSettlementFlowTests
{
    private const string InvoiceId = "invoice-flow";
    private const string StoreId = "store-1";

    [Theory]
    [InlineData("1/42")]
    [InlineData(null)]
    public void CommittingReceiverOutputsPersistsTheEventScriptAndPinnedAmountTogether(string? keyPath)
    {
        using var testContext = new RelationalPluginTestContext();
        var store = testContext.CreateStore();
        var bridgeService = testContext.CreateBridgeService();
        CreateSession(store);
        CreateBridge(testContext);
        using var settlementKey = new Key();
        var settlementScript = settlementKey.PubKey.WitHash.ScriptPubKey.ToBytes();
        var settlementKeyPath = keyPath is null ? null : new KeyPath(keyPath);

        store.CommitReceiverOutputs(store.Owner(InvoiceId), ["commit-outputs-event"], new PayjoinReceiverOutputBuilder.OutputReplacement(
                [new PayjoinTxOut(1234, settlementScript)],
                settlementScript,
                1234,
                settlementKeyPath));

        using var context = testContext.CreateDbContext();
        var events = ReadEvents(context);
        Assert.Equal(new[] { "bootstrap-event", "commit-outputs-event" }, events);
        var bridge = Assert.Single(context.AccountingBridges.Where(x => x.InvoiceId == InvoiceId));
        Assert.Equal(Convert.ToHexString(settlementScript), bridge.SettlementScript);
        Assert.Equal(keyPath, bridge.SettlementKeyPath);
        Assert.Equal(1234, bridge.EffectiveInvoiceValueSats);
    }

    [Fact]
    public void CommittingReceiverOutputsLeavesNothingBehindWhenPersistenceFails()
    {
        using var testContext = new RelationalPluginTestContext();
        var store = testContext.CreateStore();
        var bridgeService = testContext.CreateBridgeService();
        CreateSession(store);
        CreateBridge(testContext);
        using var settlementKey = new Key();
        var settlementScript = settlementKey.PubKey.WitHash.ScriptPubKey.ToBytes();
        var settlementKeyPath = new KeyPath("1/42");

        testContext.FailSaveChanges = true;
        Assert.Throws<DbUpdateException>(() => store.CommitReceiverOutputs(store.Owner(InvoiceId), ["commit-outputs-event"], new PayjoinReceiverOutputBuilder.OutputReplacement(
                [new PayjoinTxOut(1234, settlementScript)],
                settlementScript,
                1234,
                settlementKeyPath)));
        testContext.FailSaveChanges = false;

        using var context = testContext.CreateDbContext();
        Assert.Equal(new[] { "bootstrap-event" }, ReadEvents(context));
        var bridge = Assert.Single(context.AccountingBridges.Where(x => x.InvoiceId == InvoiceId));
        Assert.Null(bridge.SettlementScript);
        Assert.Null(bridge.SettlementKeyPath);
        Assert.Equal(1234, bridge.EffectiveInvoiceValueSats);
    }

    [Fact]
    public async Task FinalizingPersistsTheExpectedFinalTransactionBeforeThePostAttempt()
    {
        using var testContext = new RelationalPluginTestContext();
        var store = testContext.CreateStore();
        var bridgeService = testContext.CreateBridgeService();
        CreateSession(store);
        var proposal = CreateProposal(out var finalTransaction, out var settlementScript);
        CreateBridge(testContext, settlementScriptHex: Convert.ToHexString(settlementScript.ToBytes()));
        var relaySender = new PostAttemptRecordingRelaySender();
        var publisher = CreateInvoiceEventPublisher(out var invoiceEvents, out var aggregator);
        using var aggregatorLifetime = aggregator;
        using var subscription = aggregator.Subscribe<PayjoinInvoiceEvent>(invoiceEvents.Add);
        var finalizer = CreateFinalizer(store, bridgeService, relaySender, publisher);

        await Assert.ThrowsAsync<PostAttemptedException>(() => finalizer.FinalizeCoreAsync(
            CreateFinalizationContext(store),
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
        var notification = Assert.Single(invoiceEvents);
        Assert.Equal(PayjoinInvoiceEventKind.ProposalPrepared, notification.Kind);
        Assert.Contains(finalTransaction.GetHash().ToString(), Assert.Single(notification.Logs.ToList()).Log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FinalizingDoesNotPostWhenPersistenceFails()
    {
        using var testContext = new RelationalPluginTestContext();
        var store = testContext.CreateStore();
        var bridgeService = testContext.CreateBridgeService();
        CreateSession(store);
        var proposal = CreateProposal(out _, out var settlementScript);
        CreateBridge(testContext, settlementScriptHex: Convert.ToHexString(settlementScript.ToBytes()));
        var relaySender = new PostAttemptRecordingRelaySender();
        var publisher = CreateInvoiceEventPublisher(out var invoiceEvents, out var aggregator);
        using var aggregatorLifetime = aggregator;
        using var subscription = aggregator.Subscribe<PayjoinInvoiceEvent>(invoiceEvents.Add);
        var finalizer = CreateFinalizer(store, bridgeService, relaySender, publisher);

        testContext.FailSaveChanges = true;
        await Assert.ThrowsAsync<DbUpdateException>(() => finalizer.FinalizeCoreAsync(
            CreateFinalizationContext(store),
            persister =>
            {
                persister.Save("finalize-event");
                return proposal;
            },
            CancellationToken.None));
        testContext.FailSaveChanges = false;

        Assert.Equal(0, relaySender.PostAttempts);
        Assert.Empty(invoiceEvents);
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
        CreateBridge(testContext, settlementScriptHex: Convert.ToHexString(settlementScript.ToBytes()));
        var relaySender = new PostAttemptRecordingRelaySender();
        var publisher = CreateInvoiceEventPublisher(out var invoiceEvents, out var aggregator);
        using var aggregatorLifetime = aggregator;
        using var subscription = aggregator.Subscribe<PayjoinInvoiceEvent>(invoiceEvents.Add);
        var finalizer = CreateFinalizer(store, bridgeService, relaySender, publisher);
        await Assert.ThrowsAsync<PostAttemptedException>(() => finalizer.FinalizeCoreAsync(
            CreateFinalizationContext(store),
            persister =>
            {
                persister.Save("finalize-event");
                return proposal;
            },
            CancellationToken.None));
        var bridgeAfterFinalize = await bridgeService.TryGetByInvoiceIdAsync(InvoiceId, CancellationToken.None);

        // The replay path re-records the expectation and re-posts; with the finalization already
        // persisted it must observe the bridge up to date and change nothing.
        await finalizer.EnsureExpectedFinalTransactionAsync(CreateFinalizationContext(store), proposal, CancellationToken.None);
        await Assert.ThrowsAsync<PostAttemptedException>(() => finalizer.PostAsync(CreateFinalizationContext(store), proposal, CancellationToken.None));

        var bridgeAfterReplay = await bridgeService.TryGetByInvoiceIdAsync(InvoiceId, CancellationToken.None);
        Assert.Equal(bridgeAfterFinalize, bridgeAfterReplay);
        using var context = testContext.CreateDbContext();
        Assert.Equal(new[] { "bootstrap-event", "finalize-event" }, ReadEvents(context));
        Assert.Equal(PayjoinInvoiceEventKind.ProposalPrepared, Assert.Single(invoiceEvents).Kind);
    }

    [Fact]
    public async Task ReplayBackfillPublishesThePreparedProposalOnlyAfterPersistingItsExpectation()
    {
        using var testContext = new RelationalPluginTestContext();
        var store = testContext.CreateStore();
        var bridgeService = testContext.CreateBridgeService();
        CreateSession(store);
        var proposal = CreateProposal(out var finalTransaction, out var settlementScript);
        CreateBridge(bridgeService, settlementScriptHex: Convert.ToHexString(settlementScript.ToBytes()));
        store.AppendEventsWithAccountingUpdate(InvoiceId, ["finalize-event"], updateBridge: null);
        var publisher = CreateInvoiceEventPublisher(out var invoiceEvents, out var aggregator);
        using var aggregatorLifetime = aggregator;
        using var subscription = aggregator.Subscribe<PayjoinInvoiceEvent>(invoiceEvents.Add);
        var finalizer = CreateFinalizer(store, bridgeService, new PostAttemptRecordingRelaySender(), publisher);

        testContext.FailSaveChanges = true;
        await Assert.ThrowsAsync<DbUpdateException>(() => finalizer.EnsureExpectedFinalTransactionAsync(CreateFinalizationContext(), proposal, TestContext.Current.CancellationToken));
        testContext.FailSaveChanges = false;
        Assert.Empty(invoiceEvents);
        await finalizer.EnsureExpectedFinalTransactionAsync(CreateFinalizationContext(), proposal, TestContext.Current.CancellationToken);
        await finalizer.EnsureExpectedFinalTransactionAsync(CreateFinalizationContext(), proposal, TestContext.Current.CancellationToken);

        var notification = Assert.Single(invoiceEvents);
        Assert.Equal(PayjoinInvoiceEventKind.ProposalPrepared, notification.Kind);
        Assert.Contains(finalTransaction.GetHash().ToString(), Assert.Single(notification.Logs.ToList()).Log, StringComparison.Ordinal);
        using var persisted = testContext.CreateDbContext();
        var bridge = Assert.Single(persisted.AccountingBridges);
        Assert.Equal(finalTransaction.GetHash().ToString(), bridge.ExpectedFinalTransactionId);
        Assert.Equal(1, bridge.ExpectedFinalOutputIndex);
        Assert.Equal(new[] { "bootstrap-event", "finalize-event" }, ReadEvents(persisted));
    }

    [Fact]
    public async Task FinalizingStillPersistsAndPostsWhileTheInvoiceEventWriterIsBlocked()
    {
        using var testContext = new RelationalPluginTestContext();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new RecordingInvoiceEventWriter(evt =>
        {
            if (evt.Kind == PayjoinInvoiceEventKind.TrackingStarted)
            {
                entered.TrySetResult();
                return resume.Task;
            }
            return Task.CompletedTask;
        });
        var publisher = CreateInvoiceEventPublisher(out _, out var aggregator);
        using var aggregatorLifetime = aggregator;
        var eventService = new PayjoinInvoiceEventHostedService(aggregator, writer, NullLogger<PayjoinInvoiceEventHostedService>.Instance);
        await eventService.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var store = testContext.CreateStore();
            var bridgeService = testContext.CreateBridgeService(publisher);
            CreateSession(store);
            var proposal = CreateProposal(out var finalTransaction, out var settlementScript);
            CreateBridge(bridgeService, settlementScriptHex: Convert.ToHexString(settlementScript.ToBytes()));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var relaySender = new PostAttemptRecordingRelaySender();
            var finalizer = CreateFinalizer(store, bridgeService, relaySender, publisher);

            await Assert.ThrowsAsync<PostAttemptedException>(() => finalizer.FinalizeCoreAsync(
                CreateFinalizationContext(),
                persister =>
                {
                    persister.Save("finalize-event");
                    return proposal;
                }, TestContext.Current.CancellationToken));

            Assert.Equal(1, relaySender.PostAttempts);
            Assert.Empty(writer.Writes);
            using var persisted = testContext.CreateDbContext();
            Assert.Equal(finalTransaction.GetHash().ToString(), Assert.Single(persisted.AccountingBridges).ExpectedFinalTransactionId);
            resume.TrySetResult();
            await eventService.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(new[] { PayjoinInvoiceEventKind.TrackingStarted, PayjoinInvoiceEventKind.ProposalPrepared }, writer.Writes.Select(evt => evt.Kind));
        }
        finally
        {
            resume.TrySetResult();
            await eventService.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ReconciliationMismatchMarksTheBridgeFailedWithoutCreditingAPayment()
    {
        using var testContext = new RelationalPluginTestContext();
        var store = testContext.CreateStore();
        var bridgeService = testContext.CreateBridgeService();
        var paymentService = new ObservedValuePaymentService(observedValueSats: 999);
        CreateBridge(
            testContext,
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
            testContext,
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
        RelationalPluginTestContext fixture,
        string? settlementScriptHex = null,
        string? expectedFinalTransactionId = null,
        long? expectedFinalValueSats = null)
    {
        fixture.SeedBridgeAsync(
            new CreatePayjoinAccountingBridgeRequest(
                InvoiceId,
                StoreId,
                PayjoinConstants.BitcoinCode,
                "BTC-BTC",
                DateTimeOffset.UtcNow.AddHours(1),
                EffectiveInvoiceValueSats: 1234,
                FallbackValueSats: 1234,
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

    private static PayjoinReceiverProposalFinalizationContext CreateFinalizationContext(PayjoinReceiverSessionStore store)
    {
        return new PayjoinReceiverProposalFinalizationContext(
            store.Owner(InvoiceId),
            StoreId,
            InvoiceId,
            PayjoinConstants.BitcoinCode);
    }

    private static PayjoinReceiverProposalFinalizer CreateFinalizer(
        PayjoinReceiverSessionStore store,
        PayjoinAccountingBridgeService bridgeService,
        IPayjoinReceiverRelayRequestSender relaySender,
        IPayjoinInvoiceEventPublisher? invoiceEvents = null)
    {
        return new PayjoinReceiverProposalFinalizer(
            relaySender,
            new UnusedProposalSigner(),
            bridgeService,
            store,
            CreateNetworkProvider(),
            invoiceEvents);
    }

    private static PayjoinInvoiceEventPublisher CreateInvoiceEventPublisher(out List<PayjoinInvoiceEvent> invoiceEvents, out EventAggregator aggregator)
    {
        invoiceEvents = [];
        aggregator = new EventAggregator(new Logs());
        return new PayjoinInvoiceEventPublisher(aggregator, NullLogger<PayjoinInvoiceEventPublisher>.Instance);
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
            string sessionId,
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












}
