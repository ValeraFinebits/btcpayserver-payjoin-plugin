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
    private readonly IPayjoinReceiverWalletAdapter _walletAdapter;
    private readonly PayjoinReceiverSessionStore _sessionStore;

    public PayjoinReceiverInputSelector(
        IPayjoinReceiverWalletAdapter walletAdapter,
        PayjoinReceiverSessionStore sessionStore)
    {
        _walletAdapter = walletAdapter;
        _sessionStore = sessionStore;
    }

    public async Task<ReceiverInputContributionResult> TryContributeInputsAsync(
        WantsInputs proposal,
        string storeId,
        string invoiceId,
        DateTimeOffset reservationExpiresAt,
        CancellationToken cancellationToken)
    {
        var candidates = (await _walletAdapter.GetInputCandidatesAsync(storeId, cancellationToken).ConfigureAwait(false)).ToList();

        var contributionFailures = new List<string>();
        if (candidates.Count == 0)
        {
            return ReceiverInputContributionResult.Failure("no confirmed receiver coins available");
        }

        while (candidates.Count > 0)
        {
            PayjoinReceiverInputCandidate? selected = null;
            try
            {
                using var selectedInput = proposal.TryPreservingPrivacy(candidates.Select(candidate => candidate.Input).ToArray());
                selected = _walletAdapter.ResolveSelectedCandidate(candidates, selectedInput.Outpoint());
            }
            catch (CoinSelectionException ex)
            {
                contributionFailures.Add($"receiver input selection failed: {ex.Message}");
                break;
            }

            if (selected is null)
            {
                contributionFailures.Add("selected receiver input could not be mapped back to a wallet coin");
                break;
            }

            var selectedOutPoint = selected.Coin.OutPoint.ToString();
            WantsInputs? withInputs = null;
            try
            {
                withInputs = proposal.ContributeInputs(new[] { selected.Input });
                var contributedCoins = new[] { selected.Coin };
                if (_sessionStore.TryReserveContributedInput(storeId, invoiceId, contributedCoins[0].OutPoint, reservationExpiresAt))
                {
                    return ReceiverInputContributionResult.Success(withInputs, contributedCoins);
                }

                // Cross-session reservations can make the privacy-selected coin unavailable. Remove
                // that candidate and ask rust-payjoin to choose again from the remaining wallet inputs.
                contributionFailures.Add($"candidate '{selectedOutPoint}' reservation conflict");
                candidates.Remove(selected);
                withInputs.Dispose();
                withInputs = null;
            }
            catch (InputContributionException ex)
            {
                contributionFailures.Add($"candidate '{selectedOutPoint}' rejected: {ex.Message}");
                candidates.Remove(selected);
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

        var receiverCoins = await _walletAdapter.GetConfirmedReceiverCoinsAsync(session.StoreId, cancellationToken).ConfigureAwait(false);
        var contributedCoins = receiverCoins
            .Where(coin => coin.OutPoint.Hash == contributedOutPoint.Hash && coin.OutPoint.N == contributedOutPoint.N)
            .ToArray();
        return contributedCoins.Length > 0 ? contributedCoins : null;
    }
}
