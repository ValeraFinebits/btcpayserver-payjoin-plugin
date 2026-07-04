using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using NBitcoin;
using Xunit;
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
        using var receiverKey = new NBitcoin.Key();
        using var otherWalletKey = new NBitcoin.Key();
        var receiverScript = receiverKey.PubKey.WitHash.ScriptPubKey.ToBytes();
        var otherWalletScript = otherWalletKey.PubKey.WitHash.ScriptPubKey;
        var resolver = new PayjoinScriptOwnershipResolver(receiverScript, [otherWalletScript]);

        Assert.True(resolver.IsOwned(otherWalletScript.ToBytes()));
    }

    [Fact]
    public void OwnershipResolverDoesNotClaimForeignScripts()
    {
        using var receiverKey = new NBitcoin.Key();
        using var walletKey = new NBitcoin.Key();
        using var foreignKey = new NBitcoin.Key();
        var resolver = new PayjoinScriptOwnershipResolver(
            receiverKey.PubKey.WitHash.ScriptPubKey.ToBytes(),
            [walletKey.PubKey.WitHash.ScriptPubKey]);

        Assert.False(resolver.IsOwned(foreignKey.PubKey.WitHash.ScriptPubKey.ToBytes()));
    }

    [Fact]
    public void WalletScriptOwnedCallbackRejectsProposalInputsSpendingWalletCoins()
    {
        // The rust-payjoin callback path: a sender slipping one of the receiver's other wallet coins
        // into the original proposal must be reported as owned so check_inputs_not_owned rejects it.
        using var receiverKey = new NBitcoin.Key();
        using var plantedCoinKey = new NBitcoin.Key();
        var plantedCoinScript = plantedCoinKey.PubKey.WitHash.ScriptPubKey;
        var resolver = new PayjoinScriptOwnershipResolver(
            receiverKey.PubKey.WitHash.ScriptPubKey.ToBytes(),
            [plantedCoinScript]);
        var callback = new PayjoinReceiverStateProcessor.WalletScriptOwnedCallback(resolver);

        Assert.True(callback.Callback(plantedCoinScript.ToBytes()));
    }

    [Fact]
    public void ExtractOutputScriptsReturnsEveryOutputScript()
    {
        using var firstKey = new NBitcoin.Key();
        using var secondKey = new NBitcoin.Key();
        var tx = NBitcoin.Network.RegTest.CreateTransaction();
        tx.Inputs.Add(new OutPoint(uint256.One, 0));
        tx.Outputs.Add(NBitcoin.Money.Satoshis(1000), firstKey.PubKey.WitHash.ScriptPubKey);
        tx.Outputs.Add(NBitcoin.Money.Satoshis(2000), secondKey.PubKey.WitHash.ScriptPubKey);

        var scripts = PayjoinReceiverStateProcessor.ExtractOutputScripts(tx.ToBytes());

        Assert.Equal(2, scripts.Count);
        Assert.Equal(firstKey.PubKey.WitHash.ScriptPubKey.ToBytes(), scripts[0]);
        Assert.Equal(secondKey.PubKey.WitHash.ScriptPubKey.ToBytes(), scripts[1]);
    }

    [Fact]
    public void ExtractOutputScriptsReturnsEmptyForEmptyPayload()
    {
        Assert.Empty(PayjoinReceiverStateProcessor.ExtractOutputScripts(Array.Empty<byte>()));
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
        SystemUri? ohttpRelayUrl = null,
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
            ohttpRelayUrl ?? new SystemUri("https://relay.example/"),
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
