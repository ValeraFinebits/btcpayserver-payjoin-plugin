using BTCPayServer.Services.Wallets;
using Payjoin;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal interface IPayjoinReceiverInputSelector
{
    Task<ReceiverInputContributionResult> TryContributeInputsAsync(
        WantsInputs proposal,
        string storeId,
        string invoiceId,
        System.DateTimeOffset reservationExpiresAt,
        CancellationToken cancellationToken);

    Task<ReceivedCoin[]?> TryGetPersistedContributedCoinsAsync(
        PayjoinReceiverSessionState session,
        CancellationToken cancellationToken);
}

internal sealed class ReceiverInputContributionResult
{
    private ReceiverInputContributionResult(WantsInputs? proposalWithInputs, ReceivedCoin[]? contributedCoins, string failureMessage)
    {
        ProposalWithInputs = proposalWithInputs;
        ContributedCoins = contributedCoins;
        FailureMessage = failureMessage;
    }

    internal WantsInputs? ProposalWithInputs { get; }

    internal ReceivedCoin[]? ContributedCoins { get; }

    internal string FailureMessage { get; }

    internal static ReceiverInputContributionResult Success(WantsInputs proposalWithInputs, ReceivedCoin[] contributedCoins)
    {
        return new ReceiverInputContributionResult(proposalWithInputs, contributedCoins, string.Empty);
    }

    internal static ReceiverInputContributionResult Failure(string failureMessage)
    {
        return new ReceiverInputContributionResult(null, null, failureMessage);
    }
}
