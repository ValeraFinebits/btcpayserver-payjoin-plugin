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
    private readonly IPayjoinReceiverInputProposalOperations _proposalOperations;
    private readonly PayjoinReceiverSessionStore _sessionStore;

    public PayjoinReceiverInputSelector(
        IPayjoinReceiverWalletAdapter walletAdapter,
        IPayjoinReceiverInputProposalOperations proposalOperations,
        PayjoinReceiverSessionStore sessionStore)
    {
        _walletAdapter = walletAdapter;
        _proposalOperations = proposalOperations;
        _sessionStore = sessionStore;
    }

    public async Task<ReceiverInputContributionResult> TryContributeInputsAsync(
        WantsInputs proposal,
        string storeId,
        string invoiceId,
        DateTimeOffset reservationExpiresAt,
        CancellationToken cancellationToken)
    {
        // Retry model: the candidate set is snapshotted once per attempt and failed entries are
        // removed locally. The wallet view does not exclude coins reserved by concurrent sessions,
        // so rebuilding the set mid-attempt would re-offer the very input whose reservation just
        // failed and the deterministic library selection would pick it again. Each iteration
        // strictly shrinks the set, which bounds the loop. Freshness comes from the tier above:
        // when this attempt returns a failure, the next poll tick re-enters with a newly built
        // candidate set.
        var candidates = (await _walletAdapter.GetInputCandidatesAsync(storeId, cancellationToken).ConfigureAwait(false)).ToList();

        var contributionFailures = new List<string>();
        if (candidates.Count == 0)
        {
            return ReceiverInputContributionResult.Failure("no confirmed receiver coins available");
        }

        while (candidates.Count > 0)
        {
            PayjoinReceiverInputCandidate? selected;
            try
            {
                var selectedOutPoint = _proposalOperations.SelectPreservingPrivacy(proposal, candidates);
                selected = _walletAdapter.ResolveSelectedCandidate(candidates, selectedOutPoint);
            }
            catch (PayjoinReceiverInputSelectionException ex)
            {
                contributionFailures.Add($"receiver input selection failed: {ex.Message}");
                break;
            }

            if (selected is null)
            {
                contributionFailures.Add("selected receiver input could not be mapped back to a wallet coin");
                break;
            }

            var selectedCoinOutPoint = selected.Coin.OutPoint.ToString();
            WantsInputs? withInputs = null;
            try
            {
                withInputs = _proposalOperations.ContributeInputs(proposal, selected);
                var contributedCoins = new[] { selected.Coin };
                if (_sessionStore.TryReserveContributedInput(storeId, invoiceId, contributedCoins[0].OutPoint, reservationExpiresAt))
                {
                    return ReceiverInputContributionResult.Success(withInputs, contributedCoins);
                }

                // Cross-session reservations can make the privacy-selected coin unavailable. Remove
                // that candidate and ask rust-payjoin to choose again from the remaining wallet inputs.
                contributionFailures.Add($"candidate '{selectedCoinOutPoint}' reservation conflict");
                candidates.Remove(selected);
                withInputs?.Dispose();
                withInputs = null;
            }
            catch (PayjoinReceiverInputContributionException ex)
            {
                contributionFailures.Add($"candidate '{selectedCoinOutPoint}' rejected: {ex.Message}");
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
