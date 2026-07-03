using BTCPayServer.Plugins.Payjoin.Services;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinSegwitAssumptionTests
{
    [Fact]
    public void HasNonWitnessInputDetectsInputsWithoutWitnessData()
    {
        var tx = Network.RegTest.CreateTransaction();
        tx.Inputs.Add(new OutPoint(uint256.One, 0));
        tx.Inputs[0].WitScript = WitScript.Empty;

        Assert.True(PayjoinReceiverSessionProcessor.HasNonWitnessInput(tx));
    }

    [Fact]
    public void HasNonWitnessInputAcceptsFullyWitnessedTransactions()
    {
        using var key = new Key();
        var tx = Network.RegTest.CreateTransaction();
        tx.Inputs.Add(new OutPoint(uint256.One, 0));
        tx.Inputs[0].WitScript = new WitScript(Op.GetPushOp(new byte[71]), Op.GetPushOp(key.PubKey.ToBytes()));

        Assert.False(PayjoinReceiverSessionProcessor.HasNonWitnessInput(tx));
    }

    [Fact]
    public void HasNonWitnessInputDetectsAMixOfWitnessAndLegacyInputs()
    {
        using var key = new Key();
        var tx = Network.RegTest.CreateTransaction();
        tx.Inputs.Add(new OutPoint(uint256.One, 0));
        tx.Inputs[0].WitScript = new WitScript(Op.GetPushOp(new byte[71]), Op.GetPushOp(key.PubKey.ToBytes()));
        tx.Inputs.Add(new OutPoint(uint256.One, 1));
        tx.Inputs[1].ScriptSig = new Script(Op.GetPushOp(new byte[71]));
        tx.Inputs[1].WitScript = WitScript.Empty;

        Assert.True(PayjoinReceiverSessionProcessor.HasNonWitnessInput(tx));
    }

    [Theory]
    [InlineData("p2wpkh", true)]
    [InlineData("p2wsh", true)]
    [InlineData("taproot", true)]
    [InlineData("p2pkh", false)]
    [InlineData("p2sh-p2wpkh", false)]
    public void IsSupportedReceiverCoinAcceptsNativelySegwitScriptsOnly(string kind, bool expected)
    {
        using var key = new Key();
        Script script = kind switch
        {
            "p2wpkh" => key.PubKey.WitHash.ScriptPubKey,
            "p2wsh" => key.PubKey.ScriptPubKey.WitHash.ScriptPubKey,
            "taproot" => key.PubKey.GetTaprootFullPubKey().ScriptPubKey,
            "p2pkh" => key.PubKey.Hash.ScriptPubKey,
            "p2sh-p2wpkh" => key.PubKey.WitHash.ScriptPubKey.Hash.ScriptPubKey,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        Assert.Equal(expected, PayjoinAvailabilityService.IsSupportedReceiverCoin(script));
    }
}
