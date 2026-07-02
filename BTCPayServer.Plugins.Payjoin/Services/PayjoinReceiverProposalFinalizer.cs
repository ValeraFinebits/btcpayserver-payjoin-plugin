using BTCPayServer.Services.Wallets;
using NBitcoin;
using Payjoin;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SystemUri = System.Uri;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal sealed class PayjoinReceiverProposalFinalizer : IPayjoinReceiverProposalFinalizer
{
    private readonly IPayjoinReceiverRelayClient _relayClient;
    private readonly IPayjoinReceiverProposalSigner _proposalSigner;
    private readonly IPayjoinAccountingBridgeService _accountingBridgeService;
    private readonly BTCPayNetworkProvider _networkProvider;

    public PayjoinReceiverProposalFinalizer(
        IPayjoinReceiverRelayClient relayClient,
        IPayjoinReceiverProposalSigner proposalSigner,
        IPayjoinAccountingBridgeService accountingBridgeService,
        BTCPayNetworkProvider networkProvider)
    {
        _relayClient = relayClient;
        _proposalSigner = proposalSigner;
        _accountingBridgeService = accountingBridgeService;
        _networkProvider = networkProvider;
    }

    public async Task FinalizeAsync(
        PayjoinReceiverProposalFinalizationContext context,
        WantsFeeRange proposal,
        ReceivedCoin[] receiverCoins,
        CancellationToken cancellationToken)
    {
        // Inherit the receiver session's configured max effective fee rate (set on the ReceiverBuilder) and
        // let the minimum default to the relay floor, rather than forcing an artificial 1-10 sat/vB window
        // that fails in higher-fee environments. See PayjoinUriSessionService.DefaultMaxEffectiveFeeRateSatPerVb.
        using var transition = proposal.ApplyFeeRange(null, null);
        using var provisional = transition.Save(context.Persister);
        await FinalizeAsync(context, provisional, receiverCoins, cancellationToken).ConfigureAwait(false);
    }

    public async Task FinalizeAsync(
        PayjoinReceiverProposalFinalizationContext context,
        ProvisionalProposal proposal,
        ReceivedCoin[] receiverCoins,
        CancellationToken cancellationToken)
    {
        var btcPayNetwork = _networkProvider.GetNetwork<BTCPayNetwork>(context.CryptoCode)
            ?? throw new InvalidOperationException($"Network '{context.CryptoCode}' is not available.");
        var network = btcPayNetwork.NBitcoinNetwork;

        var signer = await _proposalSigner.CreateContributedInputSignerAsync(context.StoreId, receiverCoins, cancellationToken).ConfigureAwait(false);
        using var transition = proposal.FinalizeProposal(signer);
        using var payjoinProposal = transition.Save(context.Persister);

        await RecordExpectedFinalTransactionAsync(context, payjoinProposal, network, cancellationToken).ConfigureAwait(false);
        await PostAsync(context, payjoinProposal, cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordExpectedFinalTransactionAsync(
        PayjoinReceiverProposalFinalizationContext context,
        PayjoinProposal payjoinProposal,
        Network network,
        CancellationToken cancellationToken)
    {
        // The signed PSBT only exists after finalize_proposal runs the signing callback, so the expected
        // settlement transaction is recorded here (after Save) from the resulting proposal rather than before.
        var bridge = await _accountingBridgeService.TryGetByInvoiceIdAsync(context.InvoiceId, cancellationToken).ConfigureAwait(false);
        if (bridge is null)
        {
            return;
        }

        var finalTransaction = PSBT.Parse(payjoinProposal.Psbt(), network).GetGlobalTransaction();
        var expectedFinalOutput = TryGetSettlementOutput(bridge, finalTransaction);
        await _accountingBridgeService.SetExpectedFinalTransactionAsync(
            context.InvoiceId,
            finalTransaction.GetHash().ToString(),
            expectedFinalOutput?.Index,
            bridge.EffectiveInvoiceValueSats ?? expectedFinalOutput?.ValueSats ?? bridge.FallbackValueSats,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task PostAsync(
        PayjoinReceiverProposalFinalizationContext context,
        PayjoinProposal proposal,
        CancellationToken cancellationToken)
    {
        using var requestResponse = proposal.CreatePostRequest(context.OhttpRelayUrl.ToString());
        var responseBody = await _relayClient.SendAsync(
            new SystemUri(requestResponse.Request.Url, UriKind.Absolute),
            requestResponse.Request.ContentType,
            requestResponse.Request.Body,
            cancellationToken).ConfigureAwait(false);

        using var transition = proposal.ProcessResponse(responseBody, requestResponse.ClientResponse);
        using var _ = transition.Save(context.Persister);
    }

    private static ExpectedFinalOutput? TryGetSettlementOutput(PayjoinAccountingBridgeState bridge, Transaction finalTransaction)
    {
        if (string.IsNullOrWhiteSpace(bridge.SettlementScript))
        {
            return null;
        }

        var settlementScriptBytes = Convert.FromHexString(bridge.SettlementScript);
        if (settlementScriptBytes.Length == 0)
        {
            return null;
        }

        var settlementScript = Script.FromBytesUnsafe(settlementScriptBytes);
        return finalTransaction.Outputs
            .Select((output, index) => new ExpectedFinalOutput(index, output.Value.Satoshi, output.ScriptPubKey))
            .FirstOrDefault(output => output.ScriptPubKey == settlementScript);
    }

    private sealed record ExpectedFinalOutput(int Index, long ValueSats, Script ScriptPubKey);
}
