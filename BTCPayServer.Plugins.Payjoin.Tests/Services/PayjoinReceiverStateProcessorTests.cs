using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using NBitcoin;
using Xunit;

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
