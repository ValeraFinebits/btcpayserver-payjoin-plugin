using BTCPayServer.Services.Wallets;
using Payjoin;
using System;
using System.Threading;
using System.Threading.Tasks;
using SystemUri = System.Uri;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal sealed class PayjoinReceiverProposalFinalizer : IPayjoinReceiverProposalFinalizer
{
    private readonly IPayjoinReceiverRelayClient _relayClient;
    private readonly IPayjoinReceiverProposalSigner _proposalSigner;

    public PayjoinReceiverProposalFinalizer(
        IPayjoinReceiverRelayClient relayClient,
        IPayjoinReceiverProposalSigner proposalSigner)
    {
        _relayClient = relayClient;
        _proposalSigner = proposalSigner;
    }

    public async Task FinalizeAsync(
        PayjoinReceiverProposalFinalizationContext context,
        WantsFeeRange proposal,
        ReceivedCoin[] receiverCoins,
        CancellationToken cancellationToken)
    {
        // TODO: Replace hardcoded fee range with values from NBXplorer fee estimation.
        using var transition = proposal.ApplyFeeRange(1, 10);
        using var provisional = transition.Save(context.Persister);
        await FinalizeAsync(context, provisional, receiverCoins, cancellationToken).ConfigureAwait(false);
    }

    public async Task FinalizeAsync(
        PayjoinReceiverProposalFinalizationContext context,
        ProvisionalProposal proposal,
        ReceivedCoin[] receiverCoins,
        CancellationToken cancellationToken)
    {
        var signedPsbt = await _proposalSigner.SignProposalAsync(proposal, context.StoreId, receiverCoins, cancellationToken).ConfigureAwait(false);
        using var transition = proposal.FinalizeProposal(new SigningProcessPsbt(signedPsbt));
        using var payjoinProposal = transition.Save(context.Persister);
        await PostAsync(context, payjoinProposal, cancellationToken).ConfigureAwait(false);
    }

    public async Task PostAsync(
        PayjoinReceiverProposalFinalizationContext context,
        PayjoinProposal proposal,
        CancellationToken cancellationToken)
    {
        using var requestResponse = proposal.CreatePostRequest(context.OhttpRelayUrl.ToString());
        var responseBody = await _relayClient.SendAsync(
            new SystemUri(requestResponse.request.url, UriKind.Absolute),
            requestResponse.request.contentType,
            requestResponse.request.body,
            cancellationToken).ConfigureAwait(false);

        using var transition = proposal.ProcessResponse(responseBody, requestResponse.clientResponse);
        using var _ = transition.Save(context.Persister);
    }

    internal sealed class SigningProcessPsbt : ProcessPsbt
    {
        private readonly string _signedPsbt;

        public SigningProcessPsbt(string signedPsbt)
        {
            _signedPsbt = signedPsbt;
        }

        public string Callback(string _psbt) => _signedPsbt;
    }
}
