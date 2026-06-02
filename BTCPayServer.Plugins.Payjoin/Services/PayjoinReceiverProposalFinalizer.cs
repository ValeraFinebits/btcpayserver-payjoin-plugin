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
        var btcPayNetwork = _networkProvider.GetNetwork<BTCPayNetwork>(context.CryptoCode)
            ?? throw new InvalidOperationException($"Network '{context.CryptoCode}' is not available.");
        var network = btcPayNetwork.NBitcoinNetwork;
        var finalTransaction = PSBT.Parse(signedPsbt, network).GetGlobalTransaction();
        var bridge = await _accountingBridgeService.TryGetByInvoiceIdAsync(context.InvoiceId, cancellationToken).ConfigureAwait(false);
        if (bridge is not null)
        {
            var expectedFinalOutput = TryGetSettlementOutput(bridge, finalTransaction);
            await _accountingBridgeService.SetExpectedFinalTransactionAsync(
                context.InvoiceId,
                finalTransaction.GetHash().ToString(),
                expectedFinalOutput?.Index,
                bridge.EffectiveInvoiceValueSats ?? expectedFinalOutput?.ValueSats ?? bridge.FallbackValueSats,
                cancellationToken).ConfigureAwait(false);
        }
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
