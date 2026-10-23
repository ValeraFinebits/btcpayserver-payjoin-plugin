using BTCPayServer.Services.Wallets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PayjoinInputPair = global::Payjoin.InputPair;
using PayjoinOutPoint = global::Payjoin.OutPoint;
using PayjoinTxOut = global::Payjoin.TxOut;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal interface IPayjoinReceiverWalletAdapter
{
    Task<IReadOnlyList<PayjoinReceiverInputCandidate>> GetInputCandidatesAsync(
        string storeId,
        CancellationToken cancellationToken);

    PayjoinReceiverInputCandidate? ResolveSelectedCandidate(
        IReadOnlyList<PayjoinReceiverInputCandidate> candidates,
        PayjoinOutPoint selectedOutPoint);

    Task<ReceivedCoin[]> GetConfirmedReceiverCoinsAsync(
        string storeId,
        CancellationToken cancellationToken);
}

internal sealed class PayjoinReceiverWalletAdapter : IPayjoinReceiverWalletAdapter
{
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly PayjoinAvailabilityService _availabilityService;

    public PayjoinReceiverWalletAdapter(
        BTCPayNetworkProvider networkProvider,
        PayjoinAvailabilityService availabilityService)
    {
        _networkProvider = networkProvider;
        _availabilityService = availabilityService;
    }

    public async Task<IReadOnlyList<PayjoinReceiverInputCandidate>> GetInputCandidatesAsync(
        string storeId,
        CancellationToken cancellationToken)
    {
        var confirmed = await GetConfirmedReceiverCoinsAsync(storeId, cancellationToken).ConfigureAwait(false);
        return CreateInputCandidates(confirmed, cancellationToken);
    }

    internal static IReadOnlyList<PayjoinReceiverInputCandidate> CreateInputCandidates(
        IReadOnlyList<ReceivedCoin> coins,
        CancellationToken cancellationToken,
        Func<ReceivedCoin, PayjoinInputPair>? inputPairFactory = null)
    {
        var createInputPair = inputPairFactory ?? CreateInputPair;
        var candidates = new List<PayjoinReceiverInputCandidate>(coins.Count);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var coin in coins)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!PayjoinAvailabilityService.IsSupportedReceiverCoin(coin.ScriptPubKey))
                {
                    continue;
                }

                candidates.Add(new PayjoinReceiverInputCandidate(createInputPair(coin), coin));
            }

            cancellationToken.ThrowIfCancellationRequested();
            return candidates.ToArray();
        }
        catch
        {
            foreach (var candidate in candidates)
            {
                candidate.Input.Dispose();
            }

            throw;
        }
    }

    public PayjoinReceiverInputCandidate? ResolveSelectedCandidate(
        IReadOnlyList<PayjoinReceiverInputCandidate> candidates,
        PayjoinOutPoint selectedOutPoint)
    {
        return candidates.SingleOrDefault(candidate =>
            string.Equals(candidate.Coin.OutPoint.Hash.ToString(), selectedOutPoint.Txid, StringComparison.OrdinalIgnoreCase) &&
            candidate.Coin.OutPoint.N == selectedOutPoint.Vout);
    }

    public async Task<ReceivedCoin[]> GetConfirmedReceiverCoinsAsync(
        string storeId,
        CancellationToken cancellationToken)
    {
        var network = _networkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode);
        if (network is null)
        {
            return Array.Empty<ReceivedCoin>();
        }

        return await _availabilityService.GetConfirmedReceiverCoinsAsync(
            storeId,
            PayjoinConstants.BitcoinCode,
            network,
            cancellationToken).ConfigureAwait(false);
    }

    internal static PayjoinInputPair CreateInputPair(ReceivedCoin coin)
    {
        var outpoint = new PayjoinOutPoint(coin.OutPoint.Hash.ToString(), coin.OutPoint.N);
        var txout = new PayjoinTxOut(checked((ulong)coin.Coin.Amount.Satoshi), coin.ScriptPubKey.ToBytes());
        if (coin.ScriptPubKey.IsScriptType(NBitcoin.ScriptType.Taproot))
        {
            return PayjoinInputPair.NewP2trKeyspend(txout, outpoint);
        }
        if (coin.ScriptPubKey.IsScriptType(NBitcoin.ScriptType.P2WPKH))
        {
            return PayjoinInputPair.NewP2wpkh(txout, outpoint);
        }

        throw new NotSupportedException("The receiver input script type is not supported.");
    }
}

internal sealed record PayjoinReceiverInputCandidate(PayjoinInputPair Input, ReceivedCoin Coin)
{
    public NBitcoin.OutPoint OutPoint => Coin.OutPoint;
}
