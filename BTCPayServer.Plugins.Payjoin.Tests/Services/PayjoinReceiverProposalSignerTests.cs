using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services.Wallets;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinReceiverProposalSignerTests
{
    [Fact]
    public void EnsureContributedInputsPresentSucceedsWhenAllContributedInputsExist()
    {
        // Arrange
        var outPoint = new OutPoint(uint256.Parse("1111111111111111111111111111111111111111111111111111111111111111"), 0);
        var psbt = CreatePsbtWithInputs(outPoint);
        var receivedCoins = new[] { CreateReceivedCoin(outPoint, Money.Satoshis(50_000), CreateScript(1)) };

        // Act
        var exception = Record.Exception(() => PayjoinReceiverProposalSigner.EnsureContributedInputsPresent(psbt, receivedCoins));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void EnsureContributedInputsPresentThrowsWhenInputMissing()
    {
        // Arrange
        var missingOutPoint = new OutPoint(uint256.Parse("2222222222222222222222222222222222222222222222222222222222222222"), 1);
        var psbt = CreatePsbtWithInputs(new OutPoint(uint256.Parse("3333333333333333333333333333333333333333333333333333333333333333"), 0));
        var receivedCoins = new[] { CreateReceivedCoin(missingOutPoint, Money.Satoshis(10_000), CreateScript(2)) };

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => PayjoinReceiverProposalSigner.EnsureContributedInputsPresent(psbt, receivedCoins));

        // Assert
        Assert.Contains(missingOutPoint.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureContributedInputsPresentThrowsWhenMultipleInputsMissing()
    {
        // Arrange
        var missingOutPoint1 = new OutPoint(uint256.Parse("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), 0);
        var missingOutPoint2 = new OutPoint(uint256.Parse("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), 1);
        var psbt = CreatePsbtWithInputs(new OutPoint(uint256.Parse("cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"), 2));
        var receivedCoins =
        new[]
        {
            CreateReceivedCoin(missingOutPoint1, Money.Satoshis(10_000), CreateScript(2)),
            CreateReceivedCoin(missingOutPoint2, Money.Satoshis(20_000), CreateScript(3))
        };

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => PayjoinReceiverProposalSigner.EnsureContributedInputsPresent(psbt, receivedCoins));

        // Assert
        Assert.Contains(missingOutPoint1.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Contains(missingOutPoint2.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ClearSenderInputFinalizationClearsOnlySenderInputs()
    {
        // Arrange
        var receiverOutPoint = new OutPoint(uint256.Parse("4444444444444444444444444444444444444444444444444444444444444444"), 0);
        var senderOutPoint = new OutPoint(uint256.Parse("5555555555555555555555555555555555555555555555555555555555555555"), 1);
        var psbt = CreatePsbtWithInputs(receiverOutPoint, senderOutPoint);
        psbt.Inputs[0].FinalScriptSig = Script.Empty;
        psbt.Inputs[0].FinalScriptWitness = WitScript.Empty;
        psbt.Inputs[1].FinalScriptSig = Script.Empty;
        psbt.Inputs[1].FinalScriptWitness = WitScript.Empty;
        var receivedCoins = new[] { CreateReceivedCoin(receiverOutPoint, Money.Satoshis(20_000), CreateScript(3)) };

        // Act
        PayjoinReceiverProposalSigner.ClearSenderInputFinalization(psbt, receivedCoins);

        // Assert
        Assert.NotNull(psbt.Inputs[0].FinalScriptSig);
        Assert.NotNull(psbt.Inputs[0].FinalScriptWitness);
        Assert.Null(psbt.Inputs[1].FinalScriptSig);
        Assert.Null(psbt.Inputs[1].FinalScriptWitness);
    }

    [Fact]
    public void ClearPartialSignaturesRemovesAllPartialSigs()
    {
        // Arrange
        var psbt = CreatePsbtWithInputs(
            new OutPoint(uint256.Parse("6666666666666666666666666666666666666666666666666666666666666666"), 0),
            new OutPoint(uint256.Parse("7777777777777777777777777777777777777777777777777777777777777777"), 1));
        using var key1 = new Key();
        using var key2 = new Key();
        psbt.Inputs[0].PartialSigs.Add(key1.PubKey, new TransactionSignature(key1.Sign(uint256.One), SigHash.All));
        psbt.Inputs[1].PartialSigs.Add(key2.PubKey, new TransactionSignature(key2.Sign(uint256.One), SigHash.All));

        // Act
        PayjoinReceiverProposalSigner.ClearPartialSignatures(psbt);

        // Assert
        Assert.Empty(psbt.Inputs[0].PartialSigs);
        Assert.Empty(psbt.Inputs[1].PartialSigs);
    }

    [Fact]
    public void ClearHdKeyPathsRemovesAllInputAndOutputKeyPaths()
    {
        // Arrange
        var psbt = CreatePsbtWithInputs(new OutPoint(uint256.Parse("8888888888888888888888888888888888888888888888888888888888888888"), 0));
        using var key = new Key();
        var fingerprint = new HDFingerprint(new byte[] { 1, 2, 3, 4 });
        psbt.Inputs[0].HDKeyPaths.Add(key.PubKey, new RootedKeyPath(fingerprint, new KeyPath("0/0")));
        psbt.Outputs[0].HDKeyPaths.Add(key.PubKey, new RootedKeyPath(fingerprint, new KeyPath("1/0")));

        // Act
        PayjoinReceiverProposalSigner.ClearHdKeyPaths(psbt);

        // Assert
        Assert.Empty(psbt.Inputs[0].HDKeyPaths);
        Assert.Empty(psbt.Outputs[0].HDKeyPaths);
    }

    private static PSBT CreatePsbtWithInputs(params OutPoint[] outPoints)
    {
        var network = Network.RegTest;
        var transaction = network.CreateTransaction();
        foreach (var outPoint in outPoints)
        {
            transaction.Inputs.Add(new TxIn(outPoint));
        }

        transaction.Outputs.Add(Money.Satoshis(1000), CreateScript(9));
        return PSBT.FromTransaction(transaction, network);
    }

    private static ReceivedCoin CreateReceivedCoin(OutPoint outPoint, Money amount, Script scriptPubKey)
    {
        return new ReceivedCoin
        {
            OutPoint = outPoint,
            ScriptPubKey = scriptPubKey,
            Value = amount,
            Coin = new Coin(outPoint, new TxOut(amount, scriptPubKey)),
            Timestamp = DateTimeOffset.UtcNow,
            Confirmations = 1
        };
    }

    private static Script CreateScript(int seed)
    {
        var keyBytes = Enumerable.Repeat((byte)seed, 32).ToArray();
        using var key = new Key(keyBytes);
        return key.PubKey.WitHash.ScriptPubKey;
    }
}
