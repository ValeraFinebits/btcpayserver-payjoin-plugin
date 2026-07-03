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
using PayjoinOutPoint = Payjoin.OutPoint;
using PayjoinProposalTransition = Payjoin.PayjoinProposalTransition;
using ProcessPsbt = Payjoin.ProcessPsbt;
using RequestResponse = Payjoin.RequestResponse;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinReceiverProposalFinalizerTests
{
    [Fact]
    public async Task EnsureExpectedFinalTransactionAsyncReturnsWithoutReadingTheProposalWhenNoBridgeExists()
    {
        var bridgeService = new RecordingBridgeService(bridge: null);
        var finalizer = CreateFinalizer(bridgeService);

        await finalizer.EnsureExpectedFinalTransactionAsync(CreateContext(), new ThrowingProposal(), CancellationToken.None);

        Assert.Empty(bridgeService.SetExpectedFinalTransactionCalls);
    }

    [Fact]
    public async Task EnsureExpectedFinalTransactionAsyncSkipsTheWriteWhenTheExpectedFinalTransactionIdAlreadyMatches()
    {
        var proposal = CreateProposal(out var finalTransaction, out _);
        var bridge = CreateBridge(expectedFinalTransactionId: finalTransaction.GetHash().ToString().ToUpperInvariant());
        var bridgeService = new RecordingBridgeService(bridge);
        var finalizer = CreateFinalizer(bridgeService);

        await finalizer.EnsureExpectedFinalTransactionAsync(CreateContext(), proposal, CancellationToken.None);

        Assert.Empty(bridgeService.SetExpectedFinalTransactionCalls);
    }

    [Fact]
    public async Task EnsureExpectedFinalTransactionAsyncRecordsTheExpectedFinalTransactionWhenTheBridgeHasNone()
    {
        var proposal = CreateProposal(out var finalTransaction, out var settlementScript);
        var bridge = CreateBridge(
            expectedFinalTransactionId: null,
            settlementScript: Convert.ToHexString(settlementScript.ToBytes()),
            effectiveInvoiceValueSats: 1234);
        var bridgeService = new RecordingBridgeService(bridge);
        var finalizer = CreateFinalizer(bridgeService);

        await finalizer.EnsureExpectedFinalTransactionAsync(CreateContext(), proposal, CancellationToken.None);

        var call = Assert.Single(bridgeService.SetExpectedFinalTransactionCalls);
        Assert.Equal("invoice-1", call.InvoiceId);
        Assert.Equal(finalTransaction.GetHash().ToString(), call.ExpectedFinalTransactionId);
        Assert.Equal(1, call.ExpectedFinalOutputIndex);
        // The settlement output's value from the proposal itself wins over the pinned effective
        // invoice value: it is the exact value reconciliation should later observe on-chain.
        Assert.Equal(20_000, call.ExpectedFinalValueSats);
    }

    [Fact]
    public async Task EnsureExpectedFinalTransactionAsyncReplacesAStaleExpectedFinalTransactionId()
    {
        var proposal = CreateProposal(out var finalTransaction, out var settlementScript);
        var bridge = CreateBridge(
            expectedFinalTransactionId: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            settlementScript: Convert.ToHexString(settlementScript.ToBytes()),
            effectiveInvoiceValueSats: null);
        var bridgeService = new RecordingBridgeService(bridge);
        var finalizer = CreateFinalizer(bridgeService);

        await finalizer.EnsureExpectedFinalTransactionAsync(CreateContext(), proposal, CancellationToken.None);

        var call = Assert.Single(bridgeService.SetExpectedFinalTransactionCalls);
        Assert.Equal(finalTransaction.GetHash().ToString(), call.ExpectedFinalTransactionId);
        Assert.Equal(1, call.ExpectedFinalOutputIndex);
        // Without a pinned effective invoice value, the settlement output's value is recorded.
        Assert.Equal(20_000, call.ExpectedFinalValueSats);
    }

    private static PayjoinReceiverProposalFinalizer CreateFinalizer(IPayjoinAccountingBridgeService bridgeService)
    {
        return new PayjoinReceiverProposalFinalizer(
            new UnusedRelayRequestSender(),
            new UnusedProposalSigner(),
            bridgeService,
            // EnsureExpectedFinalTransactionAsync records through the bridge service only; the
            // session store participates in the finalize paths, which these tests do not drive.
            sessionStore: null!,
            CreateNetworkProvider());
    }

    private static PayjoinReceiverProposalFinalizationContext CreateContext()
    {
        return new PayjoinReceiverProposalFinalizationContext(
            persister: null!,
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
        public PayjoinOutPoint[] UtxosToBeLocked() => throw new NotSupportedException();
    }

    private sealed class ThrowingProposal : IPayjoinProposal
    {
        public CancelTransition Cancel() => throw new NotSupportedException();
        public RequestResponse CreatePostRequest(string ohttpRelay) => throw new NotSupportedException();
        public PayjoinProposalTransition ProcessResponse(byte[] body, ClientResponse ohttpContext) => throw new NotSupportedException();
        public string Psbt() => throw new NotSupportedException();
        public PayjoinOutPoint[] UtxosToBeLocked() => throw new NotSupportedException();
    }

    private sealed class UnusedRelayRequestSender : IPayjoinReceiverRelayRequestSender
    {
        public Task<(byte[] ResponseBody, TRequestContext RequestContext)> SendAsync<TRequestContext>(
            string storeId,
            string invoiceId,
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
        public List<(string InvoiceId, string ExpectedFinalTransactionId, long? ExpectedFinalOutputIndex, long? ExpectedFinalValueSats)> SetExpectedFinalTransactionCalls { get; } = [];

        public Task<PayjoinAccountingBridgeState?> TryGetByInvoiceIdAsync(string invoiceId, CancellationToken cancellationToken) => Task.FromResult(bridge);

        public Task<PayjoinAccountingBridgeState?> SetExpectedFinalTransactionAsync(string invoiceId, string expectedFinalTransactionId, long? expectedFinalOutputIndex, long? expectedFinalValueSats, CancellationToken cancellationToken)
        {
            SetExpectedFinalTransactionCalls.Add((invoiceId, expectedFinalTransactionId, expectedFinalOutputIndex, expectedFinalValueSats));
            return Task.FromResult<PayjoinAccountingBridgeState?>(null);
        }

        public Task<PayjoinAccountingBridgeState> CreateOrGetAsync(CreatePayjoinAccountingBridgeRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<PayjoinAccountingBridgeState>> GetPendingAsync(DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PayjoinAccountingBridgeState?> AttachFallbackAsync(string invoiceId, string fallbackTransactionId, long fallbackOutputIndex, long fallbackValueSats, long effectiveInvoiceValueSats, string? settlementScript, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PayjoinAccountingBridgeState?> SetSettlementScriptAsync(string invoiceId, string settlementScript, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PayjoinAccountingBridgeState?> MarkReconciledAsync(string invoiceId, string? expectedFinalTransactionId, long? expectedFinalOutputIndex, long? expectedFinalValueSats, DateTimeOffset reconciledAt, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PayjoinAccountingBridgeState?> MarkFailedAsync(string invoiceId, string failureMessage, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> ExpirePendingAsync(DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
