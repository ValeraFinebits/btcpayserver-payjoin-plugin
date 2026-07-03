using Payjoin;
using System;
using System.Collections.Generic;
using System.Linq;
using PayjoinOutPoint = global::Payjoin.OutPoint;

namespace BTCPayServer.Plugins.Payjoin.Services;

/// <summary>
/// Wraps the rust-payjoin proposal input operations so the selection/retry logic in
/// <see cref="PayjoinReceiverInputSelector"/> stays free of FFI types and can be unit tested.
/// </summary>
internal interface IPayjoinReceiverInputProposalOperations
{
    /// <summary>
    /// Asks rust-payjoin to choose the privacy-preserving input from the candidates.
    /// Throws <see cref="PayjoinReceiverInputSelectionException"/> when the library cannot select.
    /// </summary>
    PayjoinOutPoint SelectPreservingPrivacy(WantsInputs proposal, IReadOnlyList<PayjoinReceiverInputCandidate> candidates);

    /// <summary>
    /// Contributes the selected input to the proposal.
    /// Throws <see cref="PayjoinReceiverInputContributionException"/> when the library rejects it.
    /// </summary>
    WantsInputs ContributeInputs(WantsInputs proposal, PayjoinReceiverInputCandidate selected);
}

internal sealed class PayjoinReceiverInputProposalOperations : IPayjoinReceiverInputProposalOperations
{
    public PayjoinOutPoint SelectPreservingPrivacy(WantsInputs proposal, IReadOnlyList<PayjoinReceiverInputCandidate> candidates)
    {
        try
        {
            using var selectedInput = proposal.TryPreservingPrivacy(candidates.Select(candidate => candidate.Input).ToArray());
            return selectedInput.Outpoint();
        }
        catch (CoinSelectionException ex)
        {
            throw new PayjoinReceiverInputSelectionException(ex.Message, ex);
        }
    }

    public WantsInputs ContributeInputs(WantsInputs proposal, PayjoinReceiverInputCandidate selected)
    {
        try
        {
            return proposal.ContributeInputs(new[] { selected.Input });
        }
        catch (InputContributionException ex)
        {
            throw new PayjoinReceiverInputContributionException(ex.Message, ex);
        }
    }
}

public sealed class PayjoinReceiverInputSelectionException : Exception
{
    public PayjoinReceiverInputSelectionException()
    {
    }

    public PayjoinReceiverInputSelectionException(string message)
        : base(message)
    {
    }

    public PayjoinReceiverInputSelectionException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class PayjoinReceiverInputContributionException : Exception
{
    public PayjoinReceiverInputContributionException()
    {
    }

    public PayjoinReceiverInputContributionException(string message)
        : base(message)
    {
    }

    public PayjoinReceiverInputContributionException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
