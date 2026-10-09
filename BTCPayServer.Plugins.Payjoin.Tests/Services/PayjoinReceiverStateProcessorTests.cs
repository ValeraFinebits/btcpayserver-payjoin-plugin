using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using NBitcoin;
using Xunit;
using OhttpKeys = Payjoin.OhttpKeys;
using ReceiverBuilder = Payjoin.ReceiverBuilder;
using ReceiverPersistedException = Payjoin.ReceiverPersistedException;
using SystemUri = System.Uri;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinReceiverStateProcessorTests
{
    [Fact]
    public void OwnershipResolverTreatsInvoiceReceiverScriptAsOwned()
    {
        var receiverScript = new byte[] { 0x01, 0x02, 0x03 };
        var resolver = new PayjoinScriptOwnershipResolver(receiverScript, []);

        Assert.True(resolver.IsOwned(new byte[] { 0x01, 0x02, 0x03 }));
    }

    [Fact]
    public void OwnershipResolverRecognizesNonInvoiceWalletScriptsAsOwned()
    {
        // The core claim of the widened check: a wallet script that is NOT the invoice's receiving
        // address must still be recognized as receiver-owned, both when it holds a coin and when it
        // was resolved from the original transaction's outputs.
        using var receiverKey = new Key();
        using var otherWalletKey = new Key();
        var receiverScript = receiverKey.PubKey.WitHash.ScriptPubKey.ToBytes();
        var otherWalletScript = otherWalletKey.PubKey.WitHash.ScriptPubKey;
        var resolver = new PayjoinScriptOwnershipResolver(receiverScript, [otherWalletScript]);

        Assert.True(resolver.IsOwned(otherWalletScript.ToBytes()));
    }

    [Fact]
    public void OwnershipResolverDoesNotClaimForeignScripts()
    {
        using var receiverKey = new Key();
        using var walletKey = new Key();
        using var foreignKey = new Key();
        var resolver = new PayjoinScriptOwnershipResolver(
            receiverKey.PubKey.WitHash.ScriptPubKey.ToBytes(),
            [walletKey.PubKey.WitHash.ScriptPubKey]);

        Assert.False(resolver.IsOwned(foreignKey.PubKey.WitHash.ScriptPubKey.ToBytes()));
    }

    [Fact]
    public void WalletInputOwnedCallbackRejectsProposalInputsSpendingWalletCoins()
    {
        var walletOutPoint = new OutPoint(uint256.One, 7);
        var resolver = new PayjoinInputOwnershipResolver([walletOutPoint]);
        var callback = new PayjoinReceiverStateProcessor.WalletInputOwnedCallback(resolver);

        Assert.True(callback.Callback(new global::Payjoin.OutPoint(walletOutPoint.Hash.ToString(), walletOutPoint.N)));
        Assert.False(callback.Callback(new global::Payjoin.OutPoint(walletOutPoint.Hash.ToString(), walletOutPoint.N + 1)));
    }

    [Fact]
    public void InputOwnershipResolverRejectsMalformedTransactionIdFailClosed()
    {
        var resolver = new PayjoinInputOwnershipResolver([]);

        Assert.Throws<FormatException>(() => resolver.IsOwned("not-a-transaction-id", 0));
    }

    [Fact]
    public void WalletScriptOwnedCallbackReportsReceiverOutputs()
    {
        var receiverScript = new byte[] { 0x01, 0x02, 0x03 };
        var resolver = new PayjoinScriptOwnershipResolver(receiverScript, []);
        var callback = new PayjoinReceiverStateProcessor.WalletScriptOwnedCallback(resolver);

        Assert.True(callback.Callback(receiverScript));
        Assert.False(callback.Callback(new byte[] { 0x04, 0x05, 0x06 }));
    }

    [Fact]
    public void ExtractTransactionFactsReturnsEveryInputAndOutputScript()
    {
        using var firstKey = new Key();
        using var secondKey = new Key();
        var tx = Network.RegTest.CreateTransaction();
        var firstInput = new OutPoint(uint256.One, 0);
        var secondInput = new OutPoint(uint256.Zero, 1);
        tx.Inputs.Add(firstInput);
        tx.Inputs.Add(secondInput);
        tx.Outputs.Add(Money.Satoshis(1000), firstKey.PubKey.WitHash.ScriptPubKey);
        tx.Outputs.Add(Money.Satoshis(2000), secondKey.PubKey.WitHash.ScriptPubKey);

        var facts = PayjoinReceiverStateProcessor.ExtractTransactionFacts(tx.ToBytes());

        Assert.Equal(new[] { firstInput, secondInput }, facts.InputOutpoints);
        Assert.Equal(2, facts.OutputScripts.Count);
        Assert.Equal(firstKey.PubKey.WitHash.ScriptPubKey.ToBytes(), facts.OutputScripts[0]);
        Assert.Equal(secondKey.PubKey.WitHash.ScriptPubKey.ToBytes(), facts.OutputScripts[1]);
    }

    [Fact]
    public void ExtractTransactionFactsRejectsEmptyPayloadFailClosed()
    {
        Assert.Throws<InvalidOperationException>(() =>
            PayjoinReceiverStateProcessor.ExtractTransactionFacts(Array.Empty<byte>()));
    }

    [Fact]
    public void CloseRequestedBroadcastGuardReflectsCloseRequestedState()
    {
        var openGuard = new PayjoinReceiverStateProcessor.CloseRequestedBroadcastGuard(CreateSession(isCloseRequested: false));
        var closedGuard = new PayjoinReceiverStateProcessor.CloseRequestedBroadcastGuard(CreateSession(isCloseRequested: true));

        var open = openGuard.Callback(Array.Empty<byte>());
        var closed = closedGuard.Callback(Array.Empty<byte>());

        Assert.True(open);
        Assert.False(closed);
    }

    [Fact]
    public async Task ProcessInitializedAsyncBlocksTheRelayForTheSessionWhenTheResponseCannotBeProcessed()
    {
        // Reproduces the same-instance relay rejection: the relay accepts the POST (transport
        // succeeds) but forwards the directory's raw error, so the receiver sees an empty body
        // that fails OHTTP response processing ("Unexpected response size 0, expected 8192").
        // The failing relay must be blocked for the session so later polls rotate away from it.
        using var receiverKey = new Key();
        var receiverScript = receiverKey.PubKey.WitHash.ScriptPubKey.ToBytes();
        var rejectedRelay = new SystemUri("https://relay-1.example/");
        var otherRelay = new SystemUri("https://relay-2.example/");
        var mailroomManager = new PayjoinMailroomManager(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PayjoinMailroomManager>.Instance,
            TimeSpan.FromMinutes(10),
            (_, _, _, _) => Task.FromResult(PayjoinOhttpKeysFetchResult.RetryableFailure(new HttpRequestException("unused"))));
        var processor = new PayjoinReceiverStateProcessor(
            sessionStore: null!,
            new GarbageResponseRelaySender(rejectedRelay.AbsoluteUri),
            walletOwnershipService: null!,
            seenInputStore: null!,
            mailroomManager);

        using var ohttpKeys = OhttpKeys.Decode(Convert.FromHexString(
            "01001604ba48c49c3d4a92a3ad00ecc63a024da10ced02180c73ec12d8a7ad2cc91bb483824fe2bee8d28bfe2eb2fc6453bc4d31cd851e8a6540e86c5382af588d370957000400010003"));
        var persister = new CapturingReceiverSessionPersister();
        using var builder = new ReceiverBuilder(
            receiverKey.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest).ToString(),
            "https://directory.example/",
            ohttpKeys);
        using var bootstrap = builder.Build();
        using var initialized = bootstrap.Save(persister);
        var context = new PayjoinReceiverStateContext(
            CreateSession(),
            persister,
            receiverScript,
            "store-1",
            "invoice-1",
            _ => false);

        // The transient persisted error is the same-instance rejection surface: the relay
        // transport succeeds, but the directory's raw rejection fails OHTTP response processing.
        var thrown = await Assert.ThrowsAsync<ReceiverPersistedException.Transient>(() => processor.ProcessInitializedAsync(
            context,
            initialized,
            static (_, _, _) => Task.CompletedTask,
            CancellationToken.None));
        Assert.Contains("Unexpected response size", thrown.Message, StringComparison.Ordinal);

        Assert.Equal(otherRelay, mailroomManager.ChooseRelayForRequest([rejectedRelay, otherRelay], "invoice-1"));
        Assert.Equal(rejectedRelay, mailroomManager.ChooseRelayForRequest([rejectedRelay], "invoice-2"));
    }

    /// <summary>
    /// Simulates a relay that accepts the request but returns an unprocessable body, as happens
    /// when the directory rejects an OHTTP request from a same-instance relay.
    /// </summary>
    private sealed class GarbageResponseRelaySender(string relayUrl) : IPayjoinReceiverRelayRequestSender
    {
        public Task<(byte[] ResponseBody, TRequestContext RequestContext)> SendAsync<TRequestContext>(
            string storeId,
            string invoiceId,
            Func<string, TRequestContext> buildRequest,
            Func<TRequestContext, (SystemUri Url, string ContentType, byte[] Body)> describeRequest,
            CancellationToken cancellationToken)
            where TRequestContext : IDisposable
        {
            var requestContext = buildRequest(relayUrl);
            return Task.FromResult((Array.Empty<byte>(), requestContext));
        }
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
}
