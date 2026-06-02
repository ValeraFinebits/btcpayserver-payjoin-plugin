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
    private readonly PayjoinReceiverSessionStore _sessionStore;

    public PayjoinReceiverInputSelector(
        BTCPayNetworkProvider networkProvider,
        PayjoinAvailabilityService availabilityService,
        PayjoinReceiverSessionStore sessionStore)
    {
        _networkProvider = networkProvider;
        _availabilityService = availabilityService;
        _sessionStore = sessionStore;
    }

    public async Task<ReceiverInputContributionResult> TryContributeInputsAsync(
        WantsInputs proposal,
        string storeId,
        string invoiceId,
        DateTimeOffset reservationExpiresAt,
        CancellationToken cancellationToken)
    {
        // TODO: Restore `proposal.TryPreservingPrivacy(receiverInputs)` only after rust-payjoin/payjoin-ffi lets us identify which `ReceivedCoin` was actually selected.
        // TODO: Persist only the truly contributed coin(s) into `contributedCoins`; otherwise the signing step can treat unrelated wallet inputs as receiver-owned and produce an invalid proposal.
        var (receiverInputs, receiverCoins) = await GetReceiverInputsAsync(storeId, cancellationToken).ConfigureAwait(false);

        var contributionFailures = new List<string>();
        var orderedCandidates = receiverCoins
            .Select((coin, index) => new { coin, index })
            .OrderBy(x => x.coin.Coin.Amount.Satoshi)
            .Select(x => x.index);

        foreach (var index in orderedCandidates)
        {
            WantsInputs? withInputs = null;
            try
            {
                withInputs = proposal.ContributeInputs(new[] { receiverInputs[index] });
                var contributedCoins = new[] { receiverCoins[index] };
                if (_sessionStore.TryReserveContributedInput(storeId, invoiceId, contributedCoins[0].OutPoint, reservationExpiresAt))
                {
                    return ReceiverInputContributionResult.Success(withInputs, contributedCoins);
                }

                contributionFailures.Add($"candidate '{receiverCoins[index].OutPoint}' reservation conflict");
                withInputs.Dispose();
                withInputs = null;
            }
            catch (InputContributionException ex)
            {
                contributionFailures.Add($"candidate '{receiverCoins[index].OutPoint}' rejected: {ex.Message}");
            }
            catch
            {
                withInputs?.Dispose();
                throw;
            }
        }

        var failureMessage = contributionFailures.Count switch
        {
            > 3 => string.Join(" | ", contributionFailures.Take(3)) + " | ...",
            > 0 => string.Join(" | ", contributionFailures),
            _ => "no confirmed receiver coins available"
        };
        return ReceiverInputContributionResult.Failure(failureMessage);
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
        var network = _networkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode);
        if (network is null)
        {
            return (Array.Empty<InputPair>(), Array.Empty<ReceivedCoin>());
        }

        var confirmed = await _availabilityService.GetConfirmedReceiverCoinsAsync(storeId, PayjoinConstants.BitcoinCode, network, cancellationToken).ConfigureAwait(false);
        if (confirmed.Length == 0)
        {
            return (Array.Empty<InputPair>(), Array.Empty<ReceivedCoin>());
        }

        var inputs = confirmed
            .Select(c =>
            {
                var txin = new TxIn(
                    new OutPoint(c.OutPoint.Hash.ToString(), (uint)c.OutPoint.N),
                    Array.Empty<byte>(),
                    uint.MaxValue,
                    Array.Empty<byte[]>());
                var txout = new TxOut(checked((ulong)c.Coin.Amount.Satoshi), c.ScriptPubKey.ToBytes());
                var psbtIn = new PsbtInput(txout, null, null);
                return new InputPair(txin, psbtIn, null);
            })
            .ToArray();

        return (inputs, confirmed);
    }
}
