using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services.Wallets;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

/// <summary>
/// Exercises the plugin/rust-payjoin boundary with real FFI objects: the wallet-coin to InputPair
/// mapping and the selected-input roundtrip back to the wallet coin.
/// </summary>
public class PayjoinReceiverWalletAdapterTests
{
    [Theory]
    [InlineData(ScriptPubKeyType.Segwit)]
    [InlineData(ScriptPubKeyType.TaprootBIP86)]
    public void CreateInputPairRoundTripsTheCoinOutpointThroughTheLibrary(ScriptPubKeyType scriptPubKeyType)
    {
        var coin = CreateCoin("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 3, 25_000, scriptPubKeyType);

        using var inputPair = PayjoinReceiverWalletAdapter.CreateInputPair(coin);
        var outpoint = inputPair.Outpoint();

        Assert.Equal(coin.OutPoint.Hash.ToString(), outpoint.Txid, ignoreCase: true);
        Assert.Equal(coin.OutPoint.N, outpoint.Vout);
    }

    [Fact]
    public void ResolveSelectedCandidateMapsTheLibrarySelectionBackToTheWalletCoin()
    {
        var firstCoin = CreateCoin("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 0, 10_000);
        var secondCoin = CreateCoin("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 1, 20_000);
        using var firstInput = PayjoinReceiverWalletAdapter.CreateInputPair(firstCoin);
        using var secondInput = PayjoinReceiverWalletAdapter.CreateInputPair(secondCoin);
        var candidates = new[]
        {
            new PayjoinReceiverInputCandidate(firstInput, firstCoin),
            new PayjoinReceiverInputCandidate(secondInput, secondCoin)
        };
        var adapter = new PayjoinReceiverWalletAdapter(null!, null!);

        var resolved = adapter.ResolveSelectedCandidate(candidates, secondInput.Outpoint());

        Assert.NotNull(resolved);
        Assert.Same(secondCoin, resolved!.Coin);
    }

    [Fact]
    public void ResolveSelectedCandidateReturnsNullForAnUnknownOutpoint()
    {
        var coin = CreateCoin("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 0, 10_000);
        using var input = PayjoinReceiverWalletAdapter.CreateInputPair(coin);
        var candidates = new[] { new PayjoinReceiverInputCandidate(input, coin) };
        var adapter = new PayjoinReceiverWalletAdapter(null!, null!);

        var resolved = adapter.ResolveSelectedCandidate(
            candidates,
            new global::Payjoin.OutPoint("cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc", 9));

        Assert.Null(resolved);
    }

    private static ReceivedCoin CreateCoin(string txId, uint vout, long valueSats, ScriptPubKeyType scriptPubKeyType = ScriptPubKeyType.Segwit)
    {
        using var key = new Key();
        var outPoint = new OutPoint(uint256.Parse(txId), vout);
        var scriptPubKey = key.PubKey.GetAddress(scriptPubKeyType, Network.RegTest).ScriptPubKey;
        return new ReceivedCoin
        {
            OutPoint = outPoint,
            ScriptPubKey = scriptPubKey,
            Coin = new Coin(outPoint, new TxOut(Money.Satoshis(valueSats), scriptPubKey))
        };
    }
}
