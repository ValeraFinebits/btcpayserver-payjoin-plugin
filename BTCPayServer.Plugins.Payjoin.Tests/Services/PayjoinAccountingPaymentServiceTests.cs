using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NBXplorer;
using NBXplorer.Models;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinAccountingPaymentServiceTests
{
    private static readonly KeyPath ExpectedKeyPath = new("0/18");

    [Fact]
    public void ResolveFinalOutputIndexFallsBackToSettlementScriptWhenExpectedFinalOutputIndexMissing()
    {
        // Arrange
        var finalTransaction = Network.RegTest.CreateTransaction();
        using var receiverKey = new Key();
        using var settlementKey = new Key();
        finalTransaction.Outputs.Add(Money.Satoshis(10_000), receiverKey.PubKey.WitHash.ScriptPubKey);
        var settlementScript = settlementKey.PubKey.WitHash.ScriptPubKey;
        finalTransaction.Outputs.Add(Money.Satoshis(20_000), settlementScript);

        var bridge = CreateBridge(settlementScript: Convert.ToHexString(settlementScript.ToBytes()), expectedFinalOutputIndex: null);

        // Act
        var outputIndex = InvokeResolveFinalOutputIndex(finalTransaction, bridge);

        // Assert
        Assert.Equal(1U, outputIndex);
    }

    [Fact]
    public void ResolveFinalOutputIndexReturnsNullWhenSettlementScriptOutputIsMissing()
    {
        // Arrange
        var finalTransaction = Network.RegTest.CreateTransaction();
        using var receiverKey = new Key();
        using var settlementKey = new Key();
        finalTransaction.Outputs.Add(Money.Satoshis(10_000), receiverKey.PubKey.WitHash.ScriptPubKey);

        var settlementScript = settlementKey.PubKey.WitHash.ScriptPubKey;
        var bridge = CreateBridge(settlementScript: Convert.ToHexString(settlementScript.ToBytes()), expectedFinalOutputIndex: null);

        // Act
        var outputIndex = InvokeResolveFinalOutputIndex(finalTransaction, bridge);

        // Assert
        Assert.Null(outputIndex);
    }

    [Theory]
    [InlineData(false, true, 0L, true)]
    [InlineData(false, true, 1L, false)]
    [InlineData(false, false, 0L, false)]
    [InlineData(true, true, 0L, false)]
    [InlineData(true, false, 5L, false)]
    public void ShouldWaitForFinalTransactionConfirmationDefersOnlyWhileAnUnconfirmedFallbackPaymentIsAccounted(
        bool finalPaymentExists,
        bool trackedPaymentExists,
        long confirmations,
        bool expected)
    {
        var shouldWait = PayjoinAccountingPaymentService.ShouldWaitForFinalTransactionConfirmation(finalPaymentExists, trackedPaymentExists, confirmations);

        Assert.Equal(expected, shouldWait);
    }

    [Fact]
    public void ResolveTrackedPaymentIdReturnsNullWhenFallbackOutPointIsMissing()
    {
        var bridge = CreateBridge(settlementScript: null, expectedFinalOutputIndex: null);

        var trackedPaymentId = PayjoinAccountingPaymentService.ResolveTrackedPaymentId(bridge);

        Assert.Null(trackedPaymentId);
    }

    [Fact]
    public void ResolveTrackedPaymentIdReturnsFallbackOutPointWhenPresent()
    {
        var bridge = CreateBridge(
            settlementScript: null,
            expectedFinalOutputIndex: null,
            fallbackTransactionId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            fallbackOutputIndex: 3);

        var trackedPaymentId = PayjoinAccountingPaymentService.ResolveTrackedPaymentId(bridge);

        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-3", trackedPaymentId);
    }

    [Fact]
    public void ResolveFinalOutputIndexThrowsWhenSettlementScriptMatchesMultipleOutputs()
    {
        // Arrange
        var finalTransaction = Network.RegTest.CreateTransaction();
        using var settlementKey = new Key();
        var settlementScript = settlementKey.PubKey.WitHash.ScriptPubKey;
        finalTransaction.Outputs.Add(Money.Satoshis(10_000), settlementScript);
        finalTransaction.Outputs.Add(Money.Satoshis(20_000), settlementScript);

        var bridge = CreateBridge(settlementScript: Convert.ToHexString(settlementScript.ToBytes()), expectedFinalOutputIndex: null);

        // Act + Assert
        var ex = Assert.Throws<PayjoinAccountingReconciliationDataException>(() => InvokeResolveFinalOutputIndex(finalTransaction, bridge));
        Assert.Contains("Ambiguous settlement script persisted", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveFinalOutputIndexFallsBackToSettlementScriptWhenExpectedFinalOutputIndexPointsToDifferentScript()
    {
        // Arrange
        var finalTransaction = Network.RegTest.CreateTransaction();
        using var wrongKey = new Key();
        using var settlementKey = new Key();
        finalTransaction.Outputs.Add(Money.Satoshis(10_000), wrongKey.PubKey.WitHash.ScriptPubKey);
        var settlementScript = settlementKey.PubKey.WitHash.ScriptPubKey;
        finalTransaction.Outputs.Add(Money.Satoshis(20_000), settlementScript);

        var bridge = CreateBridge(settlementScript: Convert.ToHexString(settlementScript.ToBytes()), expectedFinalOutputIndex: 0);

        // Act
        var outputIndex = InvokeResolveFinalOutputIndex(finalTransaction, bridge);

        // Assert
        Assert.Equal(1U, outputIndex);
    }

    [Fact]
    public void ResolveFinalOutputIndexFallsBackToSettlementScriptWhenExpectedFinalOutputIndexIsOutOfRange()
    {
        // Arrange
        var finalTransaction = Network.RegTest.CreateTransaction();
        using var receiverKey = new Key();
        using var settlementKey = new Key();
        finalTransaction.Outputs.Add(Money.Satoshis(10_000), receiverKey.PubKey.WitHash.ScriptPubKey);
        var settlementScript = settlementKey.PubKey.WitHash.ScriptPubKey;
        finalTransaction.Outputs.Add(Money.Satoshis(20_000), settlementScript);

        var bridge = CreateBridge(settlementScript: Convert.ToHexString(settlementScript.ToBytes()), expectedFinalOutputIndex: 99);

        // Act
        var outputIndex = InvokeResolveFinalOutputIndex(finalTransaction, bridge);

        // Assert
        Assert.Equal(1U, outputIndex);
    }

    [Fact]
    public void ResolveFinalOutputIndexReturnsNullWhenExpectedFinalOutputIndexIsOutOfRangeAndSettlementScriptDoesNotMatch()
    {
        // Arrange
        var finalTransaction = Network.RegTest.CreateTransaction();
        using var receiverKey = new Key();
        using var settlementKey = new Key();
        finalTransaction.Outputs.Add(Money.Satoshis(10_000), receiverKey.PubKey.WitHash.ScriptPubKey);

        var settlementScript = settlementKey.PubKey.WitHash.ScriptPubKey;
        var bridge = CreateBridge(settlementScript: Convert.ToHexString(settlementScript.ToBytes()), expectedFinalOutputIndex: 99);

        // Act
        var outputIndex = InvokeResolveFinalOutputIndex(finalTransaction, bridge);

        // Assert
        Assert.Null(outputIndex);
    }

    [Fact]
    public async Task ReconcileMovesTheInvoiceFromTheFallbackPaymentToTheFinalPaymentOnceTheFinalTransactionConfirms()
    {
        // Arrange: an invoice whose accounted fallback payment is the only payment on record,
        // and a final transaction that pays the settlement script but has not confirmed yet.
        using var fixture = EndToEndFixture.Create();
        Assert.Single(fixture.World.Materialize().GetPayments(true));

        // Act + Assert: while the final transaction is unconfirmed, reconciliation waits and
        // does not touch the payment records.
        fixture.SetFinalTransactionConfirmations(0);
        var pendingResult = await fixture.Service.ReconcileWithFinalTransactionAsync(fixture.Bridge, TestContext.Current.CancellationToken);

        Assert.Null(pendingResult);
        Assert.Single(fixture.World.Rows);
        var pendingAccounted = Assert.Single(fixture.World.Materialize().GetPayments(true));
        Assert.Equal(fixture.FallbackOutPoint.ToString(), pendingAccounted.Id);

        // Act: the final transaction confirms.
        fixture.SetFinalTransactionConfirmations(1);
        var finalPayment = await fixture.Service.ReconcileWithFinalTransactionAsync(fixture.Bridge, TestContext.Current.CancellationToken);

        // Assert: the final outpoint received its own payment record, the fallback payment was
        // retired to Unaccounted, and the invoice ends up with exactly one accounted payment.
        Assert.NotNull(finalPayment);
        Assert.Equal(fixture.FinalOutPoint.ToString(), finalPayment.Id);
        Assert.Equal(2, fixture.World.Rows.Count);

        var invoice = fixture.World.Materialize();
        var accountedPayment = Assert.Single(invoice.GetPayments(true));
        Assert.Equal(fixture.FinalOutPoint.ToString(), accountedPayment.Id);
        Assert.Equal(PaymentStatus.Settled, accountedPayment.Status);
        Assert.Equal(Money.Satoshis(fixture.AccountedValueSats).ToDecimal(MoneyUnit.BTC), accountedPayment.Value);
        var paymentDetails = fixture.Handler.ParsePaymentDetails(accountedPayment.Details);
        Assert.Equal(ExpectedKeyPath, paymentDetails.KeyPath);
        Assert.Equal(18, paymentDetails.KeyIndex);

        var fallbackPayment = invoice.GetPayments(false).Single(p => p.Id == fixture.FallbackOutPoint.ToString());
        Assert.Equal(PaymentStatus.Unaccounted, fallbackPayment.Status);

        Assert.Equal(1, fixture.InvoiceNeedUpdateEvents);
        Assert.Equal(1, fixture.StalePaidOverCorrections);
    }

    [Fact]
    public async Task ReconcileRecordsTheSettlementOutputAsThePaymentDestination()
    {
        using var fixture = EndToEndFixture.Create(useDistinctInvoiceDestination: true);
        var settlementScript = Script.FromBytesUnsafe(Convert.FromHexString(fixture.Bridge.SettlementScript!));
        var settlementDestination = settlementScript.GetDestinationAddress(Network.RegTest)!.ToString();
        var invoiceDestination = fixture.World.Materialize().GetPaymentPrompt(fixture.Handler.PaymentMethodId)!.Destination;
        Assert.NotEqual(invoiceDestination, settlementDestination);
        fixture.SetFinalTransactionConfirmations(1);

        var payment = await fixture.Service.ReconcileWithFinalTransactionAsync(
            fixture.Bridge,
            TestContext.Current.CancellationToken);

        Assert.NotNull(payment);
        Assert.Equal(settlementDestination, payment.Destination);
    }

    [Fact]
    public async Task ReconcileRejectsBridgeWithoutPersistedKeyPath()
    {
        using var fixture = EndToEndFixture.Create(includePersistedKeyPath: false);
        fixture.SetFinalTransactionConfirmations(1);

        var exception = await Assert.ThrowsAsync<PayjoinAccountingReconciliationDataException>(() =>
            fixture.Service.ReconcileWithFinalTransactionAsync(
                fixture.Bridge,
                TestContext.Current.CancellationToken));

        Assert.Contains("Settlement key path is missing", exception.Message, StringComparison.Ordinal);
        Assert.Single(fixture.World.Rows);
    }

    [Fact]
    public async Task ReconcileIsIdempotentOnceTheFinalTransactionHasConfirmed()
    {
        // Arrange: reconcile once after confirmation so the fallback-to-final transition is done.
        using var fixture = EndToEndFixture.Create();
        fixture.SetFinalTransactionConfirmations(1);
        var firstResult = await fixture.Service.ReconcileWithFinalTransactionAsync(fixture.Bridge, TestContext.Current.CancellationToken);
        Assert.NotNull(firstResult);

        // Act: reconcile repeatedly in the already-reconciled state.
        var secondResult = await fixture.Service.ReconcileWithFinalTransactionAsync(fixture.Bridge, TestContext.Current.CancellationToken);
        var thirdResult = await fixture.Service.ReconcileWithFinalTransactionAsync(fixture.Bridge, TestContext.Current.CancellationToken);

        // Assert: repeated runs return the same payment, create no duplicate records, and leave
        // the final state untouched.
        Assert.NotNull(secondResult);
        Assert.NotNull(thirdResult);
        Assert.Equal(firstResult.Id, secondResult.Id);
        Assert.Equal(firstResult.Id, thirdResult.Id);
        Assert.Equal(2, fixture.World.Rows.Count);

        var invoice = fixture.World.Materialize();
        var accountedPayment = Assert.Single(invoice.GetPayments(true));
        Assert.Equal(fixture.FinalOutPoint.ToString(), accountedPayment.Id);
        Assert.Equal(PaymentStatus.Settled, accountedPayment.Status);
        var paymentDetails = fixture.Handler.ParsePaymentDetails(accountedPayment.Details);
        Assert.Equal(ExpectedKeyPath, paymentDetails.KeyPath);
        Assert.Equal(18, paymentDetails.KeyIndex);

        var fallbackPayment = invoice.GetPayments(false).Single(p => p.Id == fixture.FallbackOutPoint.ToString());
        Assert.Equal(PaymentStatus.Unaccounted, fallbackPayment.Status);
    }

    [Fact]
    public void EnsureObservedSettlementValueMatchesExpectedAcceptsAMatchingValue()
    {
        var bridge = CreateBridge(settlementScript: null, expectedFinalOutputIndex: null);

        PayjoinAccountingPaymentService.EnsureObservedSettlementValueMatchesExpected(bridge, 1000);
    }

    [Fact]
    public void EnsureObservedSettlementValueMatchesExpectedThrowsOnMismatch()
    {
        var bridge = CreateBridge(settlementScript: null, expectedFinalOutputIndex: null);

        var ex = Assert.Throws<PayjoinAccountingReconciliationDataException>(() =>
            PayjoinAccountingPaymentService.EnsureObservedSettlementValueMatchesExpected(bridge, 999));
        Assert.Contains("observed 999", ex.Message, StringComparison.Ordinal);
        Assert.Contains("expected 1000", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureObservedSettlementValueMatchesExpectedAcceptsAnyValueWithoutARecordedExpectation()
    {
        var bridge = CreateBridge(settlementScript: null, expectedFinalOutputIndex: null, expectedFinalValueSats: null);

        PayjoinAccountingPaymentService.EnsureObservedSettlementValueMatchesExpected(bridge, 999);
    }

    [Fact]
    public async Task ReconcileTagsTheSettlementTransactionAsAsyncPayjoinOnceObservable()
    {
        using var fixture = EndToEndFixture.Create();

        var beforeObservable = await fixture.Service.ReconcileWithFinalTransactionAsync(fixture.Bridge, TestContext.Current.CancellationToken);
        Assert.Null(beforeObservable);
        Assert.Empty(fixture.Labeler.Calls);

        fixture.SetFinalTransactionConfirmations(0);
        await fixture.Service.ReconcileWithFinalTransactionAsync(fixture.Bridge, TestContext.Current.CancellationToken);

        var call = Assert.Single(fixture.Labeler.Calls);
        Assert.Equal(new WalletId(fixture.Bridge.StoreId, PayjoinConstants.BitcoinCode), call.WalletId);
        Assert.Equal(fixture.FinalOutPoint.Hash, call.TransactionId);
        Assert.Equal(fixture.Bridge.InvoiceId, call.InvoiceId);
    }

    private static uint? InvokeResolveFinalOutputIndex(Transaction finalTransaction, PayjoinAccountingBridgeState bridge)
    {
        return PayjoinAccountingPaymentService.ResolveFinalOutputIndex(finalTransaction, bridge);
    }

    private static PayjoinAccountingBridgeState CreateBridge(
        string? settlementScript,
        long? expectedFinalOutputIndex,
        string? fallbackTransactionId = null,
        long? fallbackOutputIndex = null,
        long? expectedFinalValueSats = 1000)
    {
        return new PayjoinAccountingBridgeState(
            Id: 1,
            InvoiceId: "invoice-1",
            StoreId: "store-1",
            CryptoCode: PayjoinConstants.BitcoinCode,
            PaymentMethodId: "BTC-BTC",
            FallbackTransactionId: fallbackTransactionId,
            FallbackOutputIndex: fallbackOutputIndex,
            FallbackValueSats: 1000,
            EffectiveInvoiceValueSats: 1000,
            SettlementScript: settlementScript,
            ExpectedFinalTransactionId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            ExpectedFinalOutputIndex: expectedFinalOutputIndex,
            ExpectedFinalValueSats: expectedFinalValueSats,
            FailureMessage: null,
            Status: Data.PayjoinAccountingBridgeStatus.PendingFinalTransaction,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            ReconciledAt: null,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5));
    }

    // Exercises the real service against the platform's own PaymentData <-> PaymentEntity
    // conversion pipeline; only persistence and the chain view are replaced by fakes that
    // mirror the platform semantics (duplicate inserts surface as null, updates rewrite the
    // stored row, invoices materialize their payments from the stored rows).
    private sealed class EndToEndFixture : IDisposable
    {
        private readonly ScriptedWalletTransactionReader _transactionReader;
        private readonly Transaction _finalTransaction;

        private EndToEndFixture(
            PayjoinAccountingPaymentService service,
            InMemoryPaymentWorld world,
            BitcoinLikePaymentHandler handler,
            PayjoinAccountingBridgeState bridge,
            OutPoint fallbackOutPoint,
            OutPoint finalOutPoint,
            long accountedValueSats,
            ScriptedWalletTransactionReader transactionReader,
            Transaction finalTransaction,
            RecordingStalePaidOverCorrectionService staleCorrections,
            EventCounter invoiceNeedUpdateEvents,
            EventAggregator eventAggregator,
            RecordingTransactionLabeler transactionLabeler)
        {
            Service = service;
            World = world;
            Handler = handler;
            Bridge = bridge;
            FallbackOutPoint = fallbackOutPoint;
            FinalOutPoint = finalOutPoint;
            AccountedValueSats = accountedValueSats;
            _transactionReader = transactionReader;
            _finalTransaction = finalTransaction;
            _staleCorrections = staleCorrections;
            _invoiceNeedUpdateEvents = invoiceNeedUpdateEvents;
            _eventAggregator = eventAggregator;
            Labeler = transactionLabeler;
        }

        private readonly RecordingStalePaidOverCorrectionService _staleCorrections;
        private readonly EventCounter _invoiceNeedUpdateEvents;
        private readonly EventAggregator _eventAggregator;

        public void Dispose()
        {
            _eventAggregator.Dispose();
        }

        public PayjoinAccountingPaymentService Service { get; }

        public RecordingTransactionLabeler Labeler { get; }

        public InMemoryPaymentWorld World { get; }

        public BitcoinLikePaymentHandler Handler { get; }

        public PayjoinAccountingBridgeState Bridge { get; }

        public OutPoint FallbackOutPoint { get; }

        public OutPoint FinalOutPoint { get; }

        public long AccountedValueSats { get; }

        public int StalePaidOverCorrections => _staleCorrections.Count;

        public int InvoiceNeedUpdateEvents => _invoiceNeedUpdateEvents.Count;

        public void SetFinalTransactionConfirmations(long confirmations)
        {
            _transactionReader.Result = new TransactionResult
            {
                Transaction = _finalTransaction,
                TransactionHash = _finalTransaction.GetHash(),
                Confirmations = confirmations
            };
        }

        public static EndToEndFixture Create(bool includePersistedKeyPath = true, bool useDistinctInvoiceDestination = false)
        {
            const long accountedValueSats = 50_000;
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

            var paymentMethodId = PaymentMethodId.Parse("BTC-CHAIN");
            var handler = new BitcoinLikePaymentHandler(paymentMethodId, null!, network, null!, null!, null!, null!, null!);

            using var settlementKey = new Key();
            using var changeKey = new Key();
            using var invoiceKey = new Key();
            var settlementScript = settlementKey.PubKey.WitHash.ScriptPubKey;
            var invoiceScript = useDistinctInvoiceDestination
                ? invoiceKey.PubKey.WitHash.ScriptPubKey
                : settlementScript;

            var invoice = new InvoiceEntity
            {
                Id = "invoice-1",
                StoreId = "store-1",
                SpeedPolicy = SpeedPolicy.MediumSpeed
            };
            invoice.SetPaymentPrompt(paymentMethodId, new PaymentPrompt
            {
                Currency = PayjoinConstants.BitcoinCode,
                Divisibility = 8,
                Destination = invoiceScript.GetDestinationAddress(Network.RegTest)!.ToString()
            });

            var fallbackOutPoint = new OutPoint(
                uint256.Parse("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), 0);
            var finalTransaction = Network.RegTest.CreateTransaction();
            finalTransaction.Outputs.Add(Money.Satoshis(10_000), changeKey.PubKey.WitHash.ScriptPubKey);
            finalTransaction.Outputs.Add(Money.Satoshis(accountedValueSats), settlementScript);
            var finalOutPoint = new OutPoint(finalTransaction.GetHash(), 1);

            var world = new InMemoryPaymentWorld(invoice);
            var fallbackDetails = new BitcoinLikePaymentData
            {
                Outpoint = fallbackOutPoint,
                RBF = true,
                ConfirmationCount = 0
            };
            world.Rows.Add(new PaymentData
            {
                Id = fallbackOutPoint.ToString(),
                Created = DateTimeOffset.UtcNow,
                Status = PaymentStatus.Processing,
                Amount = Money.Satoshis(accountedValueSats).ToDecimal(MoneyUnit.BTC),
                Currency = PayjoinConstants.BitcoinCode
            }.Set(invoice, handler, fallbackDetails));

            var bridge = new PayjoinAccountingBridgeState(
                Id: 1,
                InvoiceId: invoice.Id,
                StoreId: invoice.StoreId,
                CryptoCode: PayjoinConstants.BitcoinCode,
                PaymentMethodId: paymentMethodId.ToString(),
                FallbackTransactionId: fallbackOutPoint.Hash.ToString(),
                FallbackOutputIndex: fallbackOutPoint.N,
                FallbackValueSats: accountedValueSats,
                EffectiveInvoiceValueSats: accountedValueSats,
                SettlementScript: Convert.ToHexString(settlementScript.ToBytes()),
                ExpectedFinalTransactionId: finalTransaction.GetHash().ToString(),
                ExpectedFinalOutputIndex: 1,
                ExpectedFinalValueSats: accountedValueSats,
                FailureMessage: null,
                Status: Data.PayjoinAccountingBridgeStatus.PendingFinalTransaction,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow,
                ReconciledAt: null,
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5))
            {
                SettlementKeyPath = includePersistedKeyPath ? ExpectedKeyPath.ToString() : null
            };

            var transactionReader = new ScriptedWalletTransactionReader();
            var transactionLabeler = new RecordingTransactionLabeler();
            var staleCorrections = new RecordingStalePaidOverCorrectionService();
            var eventAggregator = new EventAggregator(new Logs());
            var invoiceNeedUpdateEvents = new EventCounter();
            eventAggregator.Subscribe<InvoiceNeedUpdateEvent>(_ => invoiceNeedUpdateEvents.Count++);

            var service = new PayjoinAccountingPaymentService(
                world,
                staleCorrections,
                world,
                eventAggregator,
                new PaymentMethodHandlerDictionary([handler]),
                networkProvider,
                transactionReader,
                transactionLabeler,
                NullLogger<PayjoinAccountingPaymentService>.Instance);

            return new EndToEndFixture(
                service,
                world,
                handler,
                bridge,
                fallbackOutPoint,
                finalOutPoint,
                accountedValueSats,
                transactionReader,
                finalTransaction,
                staleCorrections,
                invoiceNeedUpdateEvents,
                eventAggregator,
                transactionLabeler);
        }

        private sealed class EventCounter
        {
            public int Count { get; set; }
        }
    }

    private sealed class InMemoryPaymentWorld : IPayjoinInvoiceLookup, IPayjoinPlatformPaymentRecorder
    {
        private readonly InvoiceEntity _invoice;

        public InMemoryPaymentWorld(InvoiceEntity invoice)
        {
            _invoice = invoice;
        }

        public List<PaymentData> Rows { get; } = [];

        public Task<InvoiceEntity?> GetInvoiceAsync(string invoiceId)
        {
            return Task.FromResult<InvoiceEntity?>(invoiceId == _invoice.Id ? Materialize() : null);
        }

        public Task<PaymentEntity?> AddPaymentAsync(PaymentData paymentData, HashSet<string> searchTerms)
        {
            // The platform surfaces a duplicate insert (unique key violation) as a null result.
            if (Rows.Any(row => row.Id == paymentData.Id))
            {
                return Task.FromResult<PaymentEntity?>(null);
            }

            Rows.Add(paymentData);
            return Task.FromResult<PaymentEntity?>(Materialize().GetPayments(false).Single(p => p.Id == paymentData.Id));
        }

        public Task UpdatePaymentsAsync(List<PaymentEntity> payments)
        {
            foreach (var payment in payments)
            {
                Rows.Single(row => row.Id == payment.Id).SetBlob(payment);
            }

            return Task.CompletedTask;
        }

        public InvoiceEntity Materialize()
        {
            // The platform's invoice repository materializes payments onto this property the
            // same way; the setter is only obsolete for consumers reading payments.
#pragma warning disable CS0618
            _invoice.Payments = Rows.Select(row => row.GetBlob()).ToList();
#pragma warning restore CS0618
            return _invoice;
        }
    }

    private sealed class ScriptedWalletTransactionReader : IPayjoinWalletTransactionReader
    {
        public TransactionResult? Result { get; set; }

        public Task<TransactionResult?> GetTransactionAsync(BTCPayNetwork network, uint256 transactionId, CancellationToken cancellationToken)
        {
            return Task.FromResult(Result is not null && Result.TransactionHash == transactionId ? Result : null);
        }
    }

    private sealed class RecordingTransactionLabeler : IPayjoinTransactionLabeler
    {
        public List<(WalletId WalletId, uint256 TransactionId, string InvoiceId)> Calls { get; } = [];

        public Task LabelAsyncPayjoinAsync(WalletId walletId, uint256 transactionId, string invoiceId, CancellationToken cancellationToken)
        {
            Calls.Add((walletId, transactionId, invoiceId));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingStalePaidOverCorrectionService : IPayjoinStalePaidOverCorrectionService
    {
        public int Count { get; private set; }

        public Task ClearStalePaidOverAsync(string invoiceId)
        {
            Count++;
            return Task.CompletedTask;
        }
    }
}
