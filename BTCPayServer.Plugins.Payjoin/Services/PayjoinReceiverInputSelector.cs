using BTCPayServer.Services.Wallets;
using Payjoin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal sealed class PayjoinReceiverInputSelector : IPayjoinReceiverInputSelector
{
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly PayjoinAvailabilityService _availabilityService;

    public PayjoinReceiverInputSelector(
        BTCPayNetworkProvider networkProvider,
        PayjoinAvailabilityService availabilityService)
    {
        _networkProvider = networkProvider;
        _availabilityService = availabilityService;
    }

    public async Task<ReceiverInputContributionResult> TryContributeInputsAsync(
        WantsInputs proposal,
        string storeId,
        CancellationToken cancellationToken)
    {
        var (receiverInputs, receiverCoins) = await GetReceiverInputsAsync(storeId, cancellationToken).ConfigureAwait(false);

        WantsInputs? withInputs = null;
        ReceivedCoin[]? contributedCoins = null;
        var contributionFailures = new List<string>();
        try
        {
            var orderedCandidates = receiverCoins
                .Select((coin, index) => new { coin, index })
                .OrderBy(x => x.coin.Coin.Amount.Satoshi)
                .Select(x => x.index);

            foreach (var index in orderedCandidates)
            {
                try
                {
                    withInputs = proposal.ContributeInputs(new[] { receiverInputs[index] });
                    contributedCoins = new[] { receiverCoins[index] };
                    break;
                }
                catch (InputContributionException ex)
                {
                    contributionFailures.Add($"candidate '{receiverCoins[index].OutPoint}' rejected: {ex.Message}");
                }
            }

            if (withInputs is null || contributedCoins is null)
            {
                var failureMessage = contributionFailures.Count switch
                {
                    > 3 => string.Join(" | ", contributionFailures.Take(3)) + " | ...",
                    > 0 => string.Join(" | ", contributionFailures),
                    _ => "no confirmed receiver coins available"
                };
                return ReceiverInputContributionResult.Failure(failureMessage);
            }

            return ReceiverInputContributionResult.Success(withInputs, contributedCoins);
        }
        catch
        {
            withInputs?.Dispose();
            throw;
        }
    }

    public async Task<ReceivedCoin[]?> TryGetPersistedContributedCoinsAsync(
        PayjoinReceiverSessionState session,
        CancellationToken cancellationToken)
    {
        if (!session.TryGetContributedInput(out var contributedOutPoint))
        {
            return null;
        }

        var (_, receiverCoins) = await GetReceiverInputsAsync(session.StoreId, cancellationToken).ConfigureAwait(false);
        var contributedCoins = receiverCoins
            .Where(coin => coin.OutPoint.Hash == contributedOutPoint.Hash && coin.OutPoint.N == contributedOutPoint.N)
            .ToArray();
        return contributedCoins.Length > 0 ? contributedCoins : null;
    }

    private async Task<(InputPair[] Inputs, ReceivedCoin[] Coins)> GetReceiverInputsAsync(
        string storeId,
        CancellationToken cancellationToken)
    {
        var network = _networkProvider.GetNetwork<BTCPayNetwork>("BTC");
        if (network is null)
        {
            return (Array.Empty<InputPair>(), Array.Empty<ReceivedCoin>());
        }

        var confirmed = await _availabilityService.GetConfirmedReceiverCoinsAsync(storeId, "BTC", network, cancellationToken).ConfigureAwait(false);
        if (confirmed.Length == 0)
        {
            return (Array.Empty<InputPair>(), Array.Empty<ReceivedCoin>());
        }

        var inputs = confirmed
            .Select(c =>
            {
                var txin = new PlainTxIn(
                    new PlainOutPoint(c.OutPoint.Hash.ToString(), (uint)c.OutPoint.N),
                    Array.Empty<byte>(),
                    uint.MaxValue,
                    Array.Empty<byte[]>());
                var txout = new PlainTxOut(checked((ulong)c.Coin.Amount.Satoshi), c.ScriptPubKey.ToBytes());
                var psbtIn = new PlainPsbtInput(txout, null, null);
                return new InputPair(txin, psbtIn, null);
            })
            .ToArray();

        return (inputs, confirmed);
    }
}
