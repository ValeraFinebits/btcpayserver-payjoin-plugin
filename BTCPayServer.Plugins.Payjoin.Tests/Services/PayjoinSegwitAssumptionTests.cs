using BTCPayServer.Plugins.Payjoin.Services;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinSegwitAssumptionTests
{
    [Fact]
    public void HasTxidUnstableInputDetectsLegacyInputsSigningThroughTheScriptSig()
    {
        using var key = new Key();
        var tx = Network.RegTest.CreateTransaction();
        tx.Inputs.Add(new OutPoint(uint256.One, 0));
        tx.Inputs[0].ScriptSig = new Script(Op.GetPushOp(new byte[71]), Op.GetPushOp(key.PubKey.ToBytes()));

        Assert.True(PayjoinReceiverSessionProcessor.HasTxidUnstableInput(tx));
    }

    [Fact]
    public void HasTxidUnstableInputDetectsNestedSegwitInputsDespiteTheirWitness()
    {
        // P2SH-P2WPKH finalizes with a witness AND a redeem-script push in the scriptSig; the
        // push is part of the txid preimage, so the witness alone must not count as stable.
        using var key = new Key();
        var redeemScript = key.PubKey.WitHash.ScriptPubKey;
        var tx = Network.RegTest.CreateTransaction();
        tx.Inputs.Add(new OutPoint(uint256.One, 0));
        tx.Inputs[0].ScriptSig = new Script(Op.GetPushOp(redeemScript.ToBytes()));
        tx.Inputs[0].WitScript = new WitScript(Op.GetPushOp(new byte[71]), Op.GetPushOp(key.PubKey.ToBytes()));

        Assert.True(PayjoinReceiverSessionProcessor.HasTxidUnstableInput(tx));
    }

    [Fact]
    public void HasTxidUnstableInputAcceptsNativeSegwitInputs()
    {
        using var key = new Key();
        var tx = Network.RegTest.CreateTransaction();
        tx.Inputs.Add(new OutPoint(uint256.One, 0));
        tx.Inputs[0].WitScript = new WitScript(Op.GetPushOp(new byte[71]), Op.GetPushOp(key.PubKey.ToBytes()));

        Assert.False(PayjoinReceiverSessionProcessor.HasTxidUnstableInput(tx));
    }

    [Fact]
    public void HasTxidUnstableInputDetectsAMixOfNativeAndScriptSigInputs()
    {
        using var key = new Key();
        var tx = Network.RegTest.CreateTransaction();
        tx.Inputs.Add(new OutPoint(uint256.One, 0));
        tx.Inputs[0].WitScript = new WitScript(Op.GetPushOp(new byte[71]), Op.GetPushOp(key.PubKey.ToBytes()));
        tx.Inputs.Add(new OutPoint(uint256.One, 1));
        tx.Inputs[1].ScriptSig = new Script(Op.GetPushOp(new byte[71]));

        Assert.True(PayjoinReceiverSessionProcessor.HasTxidUnstableInput(tx));
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
