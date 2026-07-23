using BTCPayServer.Plugins.Payjoin.Data;
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
    private readonly IPayjoinReceiverRelayRequestSender _relayRequestSender;
    private readonly IPayjoinReceiverProposalSigner _proposalSigner;
    private readonly IPayjoinAccountingBridgeService _accountingBridgeService;
    private readonly PayjoinReceiverSessionStore _sessionStore;
    private readonly BTCPayNetworkProvider _networkProvider;

    public PayjoinReceiverProposalFinalizer(
        IPayjoinReceiverRelayRequestSender relayRequestSender,
        IPayjoinReceiverProposalSigner proposalSigner,
        IPayjoinAccountingBridgeService accountingBridgeService,
        PayjoinReceiverSessionStore sessionStore,
        BTCPayNetworkProvider networkProvider)
    {
        _relayRequestSender = relayRequestSender;
        _proposalSigner = proposalSigner;
        _accountingBridgeService = accountingBridgeService;
        _sessionStore = sessionStore;
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
        var signer = await _proposalSigner.CreateContributedInputSignerAsync(context.StoreId, receiverCoins, cancellationToken).ConfigureAwait(false);
        using var transition = proposal.FinalizeProposal(signer);
        await FinalizeCoreAsync(context, transition.Save, cancellationToken).ConfigureAwait(false);
    }

    // Takes the transition's save step as a delegate over the generated proposal interface so the
    // persist-then-post flow can be exercised in tests, where native handles cannot be constructed.
    internal async Task FinalizeCoreAsync(
        PayjoinReceiverProposalFinalizationContext context,
        Func<CapturingReceiverSessionPersister, IPayjoinProposal> saveTransition,
        CancellationToken cancellationToken)
    {
        // The finalize event and the expected final transaction are written in one database
        // transaction: once the session can hand the proposal to the sender, the accounting side
        // already knows which transaction to reconcile against. The replay path below stays as a
        // defensive backfill.
        var capturingPersister = new CapturingReceiverSessionPersister();
        var payjoinProposal = saveTransition(capturingPersister);
        try
        {
            var bridge = await _accountingBridgeService.TryGetByInvoiceIdAsync(context.InvoiceId, cancellationToken).ConfigureAwait(false);
            if (bridge is null)
            {
                _sessionStore.AppendEventsWithAccountingUpdate(context.InvoiceId, capturingPersister.Events, updateBridge: null);
            }
            else
            {
                var btcPayNetwork = _networkProvider.GetNetwork<BTCPayNetwork>(context.CryptoCode)
                    ?? throw new InvalidOperationException($"Network '{context.CryptoCode}' is not available.");
                var finalTransaction = PSBT.Parse(payjoinProposal.Psbt(), btcPayNetwork.NBitcoinNetwork).GetGlobalTransaction();
                var finalTransactionId = finalTransaction.GetHash().ToString();
                var expectedFinalOutput = TryGetSettlementOutput(bridge, finalTransaction);
                var expectedFinalValueSats = expectedFinalOutput?.ValueSats ?? bridge.EffectiveInvoiceValueSats ?? bridge.FallbackValueSats;
                _sessionStore.AppendEventsWithAccountingUpdate(
                    context.InvoiceId,
                    capturingPersister.Events,
                    bridgeData =>
                    {
                        bridgeData.ExpectedFinalTransactionId = finalTransactionId;
                        bridgeData.ExpectedFinalOutputIndex = expectedFinalOutput?.Index;
                        bridgeData.ExpectedFinalValueSats = expectedFinalValueSats;
                        if (bridgeData.Status == PayjoinAccountingBridgeStatus.PendingFallback)
                        {
                            bridgeData.Status = PayjoinAccountingBridgeStatus.PendingFinalTransaction;
                        }
                    });
            }
        }
        catch
        {
            // Nothing was persisted, so the next tick replays back into the provisional state.
            (payjoinProposal as IDisposable)?.Dispose();
            throw;
        }

        try
        {
            await PostAsync(context, payjoinProposal, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            (payjoinProposal as IDisposable)?.Dispose();
        }
    }

    public async Task EnsureExpectedFinalTransactionAsync(
        PayjoinReceiverProposalFinalizationContext context,
        IPayjoinProposal payjoinProposal,
        CancellationToken cancellationToken)
    {
        // The signed PSBT only exists after finalize_proposal runs the signing callback, so the expected
        // settlement transaction is recorded here (after Save) from the resulting proposal rather than before.
        // The event-log save and this bridge write are separate transactions, so the proposal replay path
        // also calls this to bring the bridge up to date whenever the earlier attempt did not complete.
        var bridge = await _accountingBridgeService.TryGetByInvoiceIdAsync(context.InvoiceId, cancellationToken).ConfigureAwait(false);
        if (bridge is null)
        {
            return;
        }

        var btcPayNetwork = _networkProvider.GetNetwork<BTCPayNetwork>(context.CryptoCode)
            ?? throw new InvalidOperationException($"Network '{context.CryptoCode}' is not available.");
        var finalTransaction = PSBT.Parse(payjoinProposal.Psbt(), btcPayNetwork.NBitcoinNetwork).GetGlobalTransaction();
        var finalTransactionId = finalTransaction.GetHash().ToString();
        if (string.Equals(bridge.ExpectedFinalTransactionId, finalTransactionId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var expectedFinalOutput = TryGetSettlementOutput(bridge, finalTransaction);
        await _accountingBridgeService.SetExpectedFinalTransactionAsync(
            context.InvoiceId,
            finalTransactionId,
            expectedFinalOutput?.Index,
            expectedFinalOutput?.ValueSats ?? bridge.EffectiveInvoiceValueSats ?? bridge.FallbackValueSats,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task PostAsync(
        PayjoinReceiverProposalFinalizationContext context,
        IPayjoinProposal proposal,
        CancellationToken cancellationToken)
    {
        var relayResponse = await _relayRequestSender.SendAsync(
            context.StoreId,
            context.InvoiceId,
            proposal.CreatePostRequest,
            requestResponse => (new SystemUri(requestResponse.Request.Url, UriKind.Absolute), requestResponse.Request.ContentType, requestResponse.Request.Body),
            cancellationToken).ConfigureAwait(false);
        var responseBody = relayResponse.ResponseBody;
        var requestResponse = relayResponse.RequestContext;
        using var relayRequestContext = requestResponse;

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
