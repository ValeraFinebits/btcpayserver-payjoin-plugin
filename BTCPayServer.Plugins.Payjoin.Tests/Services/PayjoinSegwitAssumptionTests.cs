using BTCPayServer.Plugins.Payjoin.Services;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinSegwitAssumptionTests
{
    // Sender-side txid stability (legacy and nested-SegWit inputs rewriting the txid through the
    // scriptSig) is decided by rust-payjoin's ProposalTxidIsStable, which carries its own test
    // matrix for the legacy, nested, native, and mixed cases upstream.

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
