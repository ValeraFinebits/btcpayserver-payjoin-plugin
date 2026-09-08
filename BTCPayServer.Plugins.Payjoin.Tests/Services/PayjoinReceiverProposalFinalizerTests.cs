using BTCPayServer.Logging;
using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services.Wallets;
using NBitcoin;
using NBXplorer;
using Xunit;
using CancelTransition = Payjoin.CancelTransition;
using ClientResponse = Payjoin.ClientResponse;
using IPayjoinProposal = Payjoin.IPayjoinProposal;
using PayjoinProposalTransition = Payjoin.PayjoinProposalTransition;
using ProcessPsbt = Payjoin.ProcessPsbt;
using RequestResponse = Payjoin.RequestResponse;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinReceiverProposalFinalizerTests
{
    [Fact]
    public async Task EnsureExpectedFinalTransactionAsyncStopsWithoutReadingTheProposalWhenNoBridgeExists()
    {
        var bridgeService = new RecordingBridgeService(bridge: null);
        using var database = new RelationalPluginTestContext();
        var finalizer = CreateFinalizer(bridgeService, CreateStore(database, bridgeService.Bridge));

        await Assert.ThrowsAsync<PayjoinSessionStaleException>(() => finalizer.EnsureExpectedFinalTransactionAsync(CreateContext(database.CreateStore()), new ThrowingProposal(), CancellationToken.None));

    }

    [Fact]
    public async Task EnsureExpectedFinalTransactionAsyncSkipsTheWriteWhenTheExpectedFinalTransactionIdAlreadyMatches()
    {
        var proposal = CreateProposal(out var finalTransaction, out var settlementScript);
        var bridge = CreateBridge(expectedFinalTransactionId: finalTransaction.GetHash().ToString().ToUpperInvariant(), settlementScript: Convert.ToHexString(settlementScript.ToBytes()))
            with
        { ExpectedFinalOutputIndex = 1, ExpectedFinalValueSats = 20_000 };
        var bridgeService = new RecordingBridgeService(bridge);
        using var database = new RelationalPluginTestContext();
        var finalizer = CreateFinalizer(bridgeService, CreateStore(database, bridge));

        await finalizer.EnsureExpectedFinalTransactionAsync(CreateContext(database.CreateStore()), proposal, CancellationToken.None);

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnsureExpectedFinalTransactionAsyncBackfillsMissingSettlementMetadata(bool transactionIdAlreadyKnown)
    {
        var proposal = CreateProposal(out var finalTransaction, out var settlementScript);
        var bridge = CreateBridge(
            expectedFinalTransactionId: transactionIdAlreadyKnown ? finalTransaction.GetHash().ToString() : null,
            settlementScript: Convert.ToHexString(settlementScript.ToBytes()),
            effectiveInvoiceValueSats: 1234);
        var bridgeService = new RecordingBridgeService(bridge);
        using var database = new RelationalPluginTestContext();
        var finalizer = CreateFinalizer(bridgeService, CreateStore(database, bridge));

        await finalizer.EnsureExpectedFinalTransactionAsync(CreateContext(database.CreateStore()), proposal, CancellationToken.None);

        using var db = database.CreateDbContext();
        var call = Assert.Single(db.AccountingBridges);
        Assert.Equal("invoice-1", call.InvoiceId);
        Assert.Equal(finalTransaction.GetHash().ToString(), call.ExpectedFinalTransactionId);
        Assert.Equal(1, call.ExpectedFinalOutputIndex);
        // The settlement output's value from the proposal itself wins over the pinned effective
        // invoice value: it is the exact value reconciliation should later observe on-chain.
        Assert.Equal(20_000, call.ExpectedFinalValueSats);
    }

    [Fact]
    public async Task EnsureExpectedFinalTransactionAsyncRetainsAConflictingExpectedFinalTransactionId()
    {
        var proposal = CreateProposal(out var finalTransaction, out var settlementScript);
        var bridge = CreateBridge(
            expectedFinalTransactionId: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            settlementScript: Convert.ToHexString(settlementScript.ToBytes()),
            effectiveInvoiceValueSats: null);
        var bridgeService = new RecordingBridgeService(bridge);
        using var database = new RelationalPluginTestContext();
        var finalizer = CreateFinalizer(bridgeService, CreateStore(database, bridge));

        await Assert.ThrowsAsync<PayjoinAccountingReconciliationDataException>(() => finalizer.EnsureExpectedFinalTransactionAsync(CreateContext(database.CreateStore()), proposal, CancellationToken.None));

        using var db = database.CreateDbContext();
        Assert.Equal(bridge.ExpectedFinalTransactionId, db.AccountingBridges.Single().ExpectedFinalTransactionId);
    }

    private static PayjoinReceiverProposalFinalizer CreateFinalizer(IPayjoinAccountingBridgeService bridgeService, PayjoinReceiverSessionStore store)
    {
        return new PayjoinReceiverProposalFinalizer(
            new UnusedRelayRequestSender(),
            new UnusedProposalSigner(),
            bridgeService,
            sessionStore: store,
            CreateNetworkProvider());
    }

    private static PayjoinReceiverSessionStore CreateStore(RelationalPluginTestContext database, PayjoinAccountingBridgeState? bridge)
    {
        var store = database.CreateStore();
        store.GetOrCreateSession("invoice-1", "receiver", "store-1", DateTimeOffset.UtcNow.AddHours(1), ["bootstrap"]);
        if (bridge is not null)
        {
            using var db = database.CreateDbContext();
            db.AccountingBridges.Add(new PayjoinAccountingBridgeData
            {
                InvoiceId = bridge.InvoiceId,
                StoreId = bridge.StoreId,
                CryptoCode = bridge.CryptoCode,
                PaymentMethodId = bridge.PaymentMethodId,
                SettlementScript = bridge.SettlementScript,
                EffectiveInvoiceValueSats = bridge.EffectiveInvoiceValueSats,
                ExpectedFinalTransactionId = bridge.ExpectedFinalTransactionId,
                Status = bridge.Status,
                CreatedAt = bridge.CreatedAt,
                UpdatedAt = bridge.UpdatedAt
            });
            db.SaveChanges();
        }
        return store;
    }

    private static PayjoinReceiverProposalFinalizationContext CreateContext(PayjoinReceiverSessionStore store)
    {
        return new PayjoinReceiverProposalFinalizationContext(
            store.Owner("invoice-1"),
            "store-1",
            "invoice-1",
            PayjoinConstants.BitcoinCode);
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

    private static PayjoinAccountingBridgeState CreateBridge(
        string? expectedFinalTransactionId,
        string? settlementScript = null,
        long? effectiveInvoiceValueSats = 1000)
    {
        return new PayjoinAccountingBridgeState(
            Id: 1,
            InvoiceId: "invoice-1",
            StoreId: "store-1",
            CryptoCode: PayjoinConstants.BitcoinCode,
            PaymentMethodId: "BTC-BTC",
            FallbackTransactionId: null,
            FallbackOutputIndex: null,
            FallbackValueSats: 900,
            EffectiveInvoiceValueSats: effectiveInvoiceValueSats,
            SettlementScript: settlementScript,
            ExpectedFinalTransactionId: expectedFinalTransactionId,
            ExpectedFinalOutputIndex: null,
            ExpectedFinalValueSats: null,
            FailureMessage: null,
            Status: PayjoinAccountingBridgeStatus.PendingFinalTransaction,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            ReconciledAt: null,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5));
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

    private sealed class FixedPsbtProposal(string psbt) : IPayjoinProposal
    {
        public CancelTransition Cancel() => throw new NotSupportedException();
        public RequestResponse CreatePostRequest(string ohttpRelay) => throw new NotSupportedException();
        public PayjoinProposalTransition ProcessResponse(byte[] body, ClientResponse ohttpContext) => throw new NotSupportedException();
        public string Psbt() => psbt;
        public bool ProposalTxidIsStable() => throw new NotSupportedException();
    }

    private sealed class ThrowingProposal : IPayjoinProposal
    {
        public CancelTransition Cancel() => throw new NotSupportedException();
        public RequestResponse CreatePostRequest(string ohttpRelay) => throw new NotSupportedException();
        public PayjoinProposalTransition ProcessResponse(byte[] body, ClientResponse ohttpContext) => throw new NotSupportedException();
        public string Psbt() => throw new NotSupportedException();
        public bool ProposalTxidIsStable() => throw new NotSupportedException();
    }

    private sealed class UnusedRelayRequestSender : IPayjoinReceiverRelayRequestSender
    {
        public Task<(byte[] ResponseBody, TRequestContext RequestContext)> SendAsync<TRequestContext>(
            string storeId,
            string sessionId,
            Func<string, TRequestContext> buildRequest,
            Func<TRequestContext, (Uri Url, string ContentType, byte[] Body)> describeRequest,
            CancellationToken cancellationToken)
            where TRequestContext : IDisposable => throw new NotSupportedException();
    }

    private sealed class UnusedProposalSigner : IPayjoinReceiverProposalSigner
    {
        public Task<ProcessPsbt> CreateContributedInputSignerAsync(string storeId, ReceivedCoin[] receiverCoins, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingBridgeService(PayjoinAccountingBridgeState? bridge) : IPayjoinAccountingBridgeService
    {
        public PayjoinAccountingBridgeState? Bridge => bridge;

        public Task<PayjoinAccountingBridgeState?> TryGetByInvoiceIdAsync(string invoiceId, CancellationToken cancellationToken) => Task.FromResult(bridge);

        public Task<IReadOnlyCollection<PayjoinAccountingBridgeState>> GetPendingReconciliationAsync(DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PayjoinAccountingBridgeState?> SetSettlementScriptAsync(string invoiceId, string settlementScript, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PayjoinAccountingBridgeState?> MarkReconciledAsync(string invoiceId, string? expectedFinalTransactionId, long? expectedFinalOutputIndex, long? expectedFinalValueSats, DateTimeOffset reconciledAt, CancellationToken cancellationToken, DateTimeOffset? expectedUpdatedAt = null) => throw new NotSupportedException();
        public Task<PayjoinAccountingBridgeState?> MarkFailedAsync(string invoiceId, string failureMessage, CancellationToken cancellationToken, Guid? sessionInstanceId = null, DateTimeOffset? expectedUpdatedAt = null) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<PayjoinAccountingBridgeState>> ExpirePendingAsync(DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PayjoinAccountingBridgeAttentionResult> GetRequiringAttentionAsync(string storeId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PayjoinAccountingBridgeState?> TryRetryAsync(string invoiceId, string storeId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
