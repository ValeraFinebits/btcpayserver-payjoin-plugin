using BTCPayServer.Services.Wallets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PayjoinInputPair = global::Payjoin.InputPair;
using PayjoinOutPoint = global::Payjoin.OutPoint;
using PayjoinPsbtInput = global::Payjoin.PsbtInput;
using PayjoinTxIn = global::Payjoin.TxIn;
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
        return confirmed
            .Select(coin => new PayjoinReceiverInputCandidate(CreateInputPair(coin), coin))
            .ToArray();
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

    private static PayjoinInputPair CreateInputPair(ReceivedCoin coin)
    {
        var txin = new PayjoinTxIn(
            new PayjoinOutPoint(coin.OutPoint.Hash.ToString(), coin.OutPoint.N),
            Array.Empty<byte>(),
            uint.MaxValue,
            Array.Empty<byte[]>());
        var txout = new PayjoinTxOut(checked((ulong)coin.Coin.Amount.Satoshi), coin.ScriptPubKey.ToBytes());
        var psbtIn = new PayjoinPsbtInput(txout, null, null);
        return new PayjoinInputPair(txin, psbtIn, null);
    }
}

internal sealed record PayjoinReceiverInputCandidate(PayjoinInputPair Input, ReceivedCoin Coin)
{
    public NBitcoin.OutPoint OutPoint => Coin.OutPoint;
}
