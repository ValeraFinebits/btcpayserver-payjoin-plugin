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

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void CreateInputCandidatesKeepsUsableInputsWhenAnUnsupportedCoinIsPresent(int unsupportedPosition)
    {
        var firstCoin = CreateCoin("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 0, 10_000);
        var secondCoin = CreateCoin("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 1, 20_000, ScriptPubKeyType.TaprootBIP86);
        var coins = new List<ReceivedCoin> { firstCoin, secondCoin };
        coins.Insert(unsupportedPosition, CreateUnsupportedCoin(ScriptType.P2WSH));

        var candidates = PayjoinReceiverWalletAdapter.CreateInputCandidates(coins, CancellationToken.None);
        try
        {
            Assert.Collection(candidates,
                candidate => Assert.Same(firstCoin, candidate.Coin),
                candidate => Assert.Same(secondCoin, candidate.Coin));
            foreach (var candidate in candidates)
            {
                var outpoint = candidate.Input.Outpoint();
                Assert.Equal(candidate.Coin.OutPoint.Hash.ToString(), outpoint.Txid, ignoreCase: true);
                Assert.Equal(candidate.Coin.OutPoint.N, outpoint.Vout);
            }
        }
        finally
        {
            foreach (var candidate in candidates)
            {
                candidate.Input.Dispose();
            }
        }
    }

    [Fact]
    public void CreateInputCandidatesDoesNotConstructUnsupportedCoins()
    {
        var coins = new[]
        {
            CreateUnsupportedCoin(ScriptType.P2WSH),
            CreateUnsupportedCoin(ScriptType.P2PKH),
            CreateUnsupportedCoin(ScriptType.P2SH)
        };

        var candidates = PayjoinReceiverWalletAdapter.CreateInputCandidates(coins, CancellationToken.None,
            _ => throw new InvalidOperationException("Unsupported coins must not reach the input factory."));

        Assert.Empty(candidates);
    }

    [Fact]
    public void CreateInputCandidatesReturnsAnEmptySnapshotWhenNoCoinsExist()
    {
        var candidates = PayjoinReceiverWalletAdapter.CreateInputCandidates(Array.Empty<ReceivedCoin>(), CancellationToken.None);

        Assert.Empty(candidates);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void CreateInputCandidatesDisposesPreviousInputsAndPropagatesUnexpectedErrors(int successfulInputCount)
    {
        var coins = CreateSupportedCoins();
        var createdInputs = new List<global::Payjoin.InputPair>();
        var expectedError = new InvalidOperationException("Unexpected input construction failure.");
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                PayjoinReceiverWalletAdapter.CreateInputCandidates(coins, CancellationToken.None, coin =>
                {
                    if (createdInputs.Count == successfulInputCount)
                    {
                        throw expectedError;
                    }

                    var input = PayjoinReceiverWalletAdapter.CreateInputPair(coin);
                    createdInputs.Add(input);
                    return input;
                }));

            Assert.Same(expectedError, error);
            Assert.Equal(successfulInputCount, createdInputs.Count);
            Assert.All(createdInputs, input => Assert.Throws<ObjectDisposedException>(() => input.Outpoint()));
        }
        finally
        {
            foreach (var input in createdInputs)
            {
                input.Dispose();
            }
        }
    }

    [Fact]
    public void CreateInputCandidatesDoesNotSkipUnexpectedInvalidPsbtInputErrors()
    {
        var coins = CreateSupportedCoins();
        var createdInputs = new List<global::Payjoin.InputPair>();
        global::Payjoin.InputPairException.InvalidPsbtInput? nativeError = null;
        try
        {
            var error = Assert.Throws<global::Payjoin.InputPairException.InvalidPsbtInput>(() =>
                PayjoinReceiverWalletAdapter.CreateInputCandidates(coins, CancellationToken.None, coin =>
                {
                    if (createdInputs.Count == 1)
                    {
                        try
                        {
                            return global::Payjoin.InputPair.NewP2wpkh(
                                new global::Payjoin.TxOut(checked((ulong)coin.Coin.Amount.Satoshi), coin.ScriptPubKey.ToBytes()),
                                new global::Payjoin.OutPoint(coin.OutPoint.Hash.ToString(), coin.OutPoint.N));
                        }
                        catch (global::Payjoin.InputPairException.InvalidPsbtInput ex)
                        {
                            nativeError = ex;
                            throw;
                        }
                    }

                    var input = PayjoinReceiverWalletAdapter.CreateInputPair(coin);
                    createdInputs.Add(input);
                    return input;
                }));

            Assert.Same(nativeError, error);
            var createdInput = Assert.Single(createdInputs);
            Assert.Throws<ObjectDisposedException>(() => createdInput.Outpoint());
        }
        finally
        {
            nativeError?.Dispose();
            foreach (var input in createdInputs)
            {
                input.Dispose();
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void CreateInputCandidatesDisposesInputsWhenCancelledDuringConstruction(int coinCount)
    {
        using var cancellation = new CancellationTokenSource();
        var createdInputs = new List<global::Payjoin.InputPair>();
        try
        {
            var error = Assert.ThrowsAny<OperationCanceledException>(() =>
                PayjoinReceiverWalletAdapter.CreateInputCandidates(CreateSupportedCoins().Take(coinCount).ToArray(), cancellation.Token, coin =>
                {
                    var input = PayjoinReceiverWalletAdapter.CreateInputPair(coin);
                    createdInputs.Add(input);
                    cancellation.Cancel();
                    return input;
                }));

            Assert.Equal(cancellation.Token, error.CancellationToken);
            var createdInput = Assert.Single(createdInputs);
            Assert.Throws<ObjectDisposedException>(() => createdInput.Outpoint());
        }
        finally
        {
            foreach (var input in createdInputs)
            {
                input.Dispose();
            }
        }
    }

    [Fact]
    public void CreateInputCandidatesObservesCancellationForAnEmptySnapshot()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = Assert.ThrowsAny<OperationCanceledException>(() =>
            PayjoinReceiverWalletAdapter.CreateInputCandidates(Array.Empty<ReceivedCoin>(), cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
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

    private static ReceivedCoin[] CreateSupportedCoins()
    {
        return
        [
            CreateCoin("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 0, 10_000),
            CreateCoin("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 1, 20_000, ScriptPubKeyType.TaprootBIP86),
            CreateCoin("cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc", 2, 30_000)
        ];
    }

    private static ReceivedCoin CreateUnsupportedCoin(ScriptType scriptType)
    {
        using var key = new Key();
        var script = scriptType switch
        {
            ScriptType.P2WSH => key.PubKey.ScriptPubKey.WitHash.ScriptPubKey,
            ScriptType.P2PKH => key.PubKey.Hash.ScriptPubKey,
            ScriptType.P2SH => key.PubKey.WitHash.ScriptPubKey.Hash.ScriptPubKey,
            _ => throw new ArgumentOutOfRangeException(nameof(scriptType))
        };
        return CreateCoin("dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd", 3, 40_000, script);
    }

    private static ReceivedCoin CreateCoin(string txId, uint vout, long valueSats, ScriptPubKeyType scriptPubKeyType = ScriptPubKeyType.Segwit)
    {
        using var key = new Key();
        var scriptPubKey = key.PubKey.GetAddress(scriptPubKeyType, Network.RegTest).ScriptPubKey;
        return CreateCoin(txId, vout, valueSats, scriptPubKey);
    }

    private static ReceivedCoin CreateCoin(string txId, uint vout, long valueSats, Script scriptPubKey)
    {
        var outPoint = new OutPoint(uint256.Parse(txId), vout);
        return new ReceivedCoin
        {
            OutPoint = outPoint,
            ScriptPubKey = scriptPubKey,
            Coin = new Coin(outPoint, new TxOut(Money.Satoshis(valueSats), scriptPubKey))
        };
    }
}
