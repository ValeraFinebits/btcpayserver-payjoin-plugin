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
        RequireCurrentContext(context);
        if (await _accountingBridgeService.TryGetByInvoiceIdAsync(context.InvoiceId, cancellationToken).ConfigureAwait(false) is null)
            throw new PayjoinAccountingReconciliationDataException("Receiver accounting bridge is missing; manual recovery required.");
        var signer = await _proposalSigner.CreateContributedInputSignerAsync(context.StoreId, receiverCoins, cancellationToken).ConfigureAwait(false);
        RequireCurrentContext(context);
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
        RequireCurrentContext(context);
        var bridge = await _accountingBridgeService.TryGetByInvoiceIdAsync(context.InvoiceId, cancellationToken).ConfigureAwait(false)
            ?? throw new PayjoinAccountingReconciliationDataException("Receiver accounting bridge is missing; manual recovery required.");
        RequireCurrentContext(context);
        var capturingPersister = new CapturingReceiverSessionPersister();
        var payjoinProposal = saveTransition(capturingPersister);
        try
        {
            var btcPayNetwork = _networkProvider.GetNetwork<BTCPayNetwork>(context.CryptoCode)
                ?? throw new InvalidOperationException($"Network '{context.CryptoCode}' is not available.");
            var finalTransaction = PSBT.Parse(payjoinProposal.Psbt(), btcPayNetwork.NBitcoinNetwork).GetGlobalTransaction();
            var finalTransactionId = finalTransaction.GetHash().ToString();
            var expectedFinalOutput = TryGetSettlementOutput(bridge, finalTransaction);
            if (expectedFinalOutput is null || bridge.ExpectedFinalTransactionId is { } expected &&
                !string.Equals(expected, finalTransactionId, StringComparison.OrdinalIgnoreCase))
                throw new PayjoinAccountingReconciliationDataException("Proposal contradicts the saved settlement; manual recovery required.");
            _sessionStore.CommitProposal(context.Persister, capturingPersister.Events,
                new PayjoinReceiverSessionStore.ProposalSettlement(finalTransactionId, expectedFinalOutput.Index, expectedFinalOutput.ValueSats));
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
        RequireCurrentContext(context);
        // Normal finalization commits the proposal, expectation and history protection together.
        // Backfill older event logs before reposting; this write also protects the retained session
        // against deletion and fails if the session has already disappeared.
        var bridge = await _accountingBridgeService.TryGetByInvoiceIdAsync(context.InvoiceId, cancellationToken).ConfigureAwait(false);
        if (bridge is null)
        {
            throw new PayjoinAccountingReconciliationDataException("Receiver accounting bridge is missing; manual recovery required.");
        }

        var btcPayNetwork = _networkProvider.GetNetwork<BTCPayNetwork>(context.CryptoCode)
            ?? throw new InvalidOperationException($"Network '{context.CryptoCode}' is not available.");
        var finalTransaction = PSBT.Parse(payjoinProposal.Psbt(), btcPayNetwork.NBitcoinNetwork).GetGlobalTransaction();
        var finalTransactionId = finalTransaction.GetHash().ToString();
        var actualOutput = TryGetSettlementOutput(bridge, finalTransaction);
        if (actualOutput is null || bridge.ExpectedFinalOutputIndex.HasValue && bridge.ExpectedFinalOutputIndex != actualOutput.Index ||
            bridge.ExpectedFinalValueSats.HasValue && bridge.ExpectedFinalValueSats != actualOutput.ValueSats)
            throw new PayjoinAccountingReconciliationDataException("Saved settlement contradicts this proposal.");
        var expectedIdMatches = string.Equals(bridge.ExpectedFinalTransactionId, finalTransactionId, StringComparison.OrdinalIgnoreCase);
        if (expectedIdMatches && bridge.ExpectedFinalOutputIndex.HasValue && bridge.ExpectedFinalValueSats.HasValue)
        {
            return;
        }

        if (bridge.ExpectedFinalTransactionId is not null && !expectedIdMatches)
            throw new PayjoinAccountingReconciliationDataException("Saved expected transaction contradicts this proposal; manual recovery required.");

        _sessionStore.BackfillProposalExpectation(context.Persister,
            new PayjoinReceiverSessionStore.ProposalSettlement(finalTransactionId, actualOutput.Index, actualOutput.ValueSats));
    }

    public async Task PostAsync(
        PayjoinReceiverProposalFinalizationContext context,
        IPayjoinProposal proposal,
        CancellationToken cancellationToken)
    {
        RequireCurrentContext(context);
        var bridge = await _accountingBridgeService.TryGetByInvoiceIdAsync(context.InvoiceId, cancellationToken).ConfigureAwait(false);
        if (bridge is null) throw new PayjoinAccountingReconciliationDataException("Receiver accounting bridge is missing; manual recovery required.");
        RequireCurrentContext(context);
        var relayResponse = await _relayRequestSender.SendAsync(
            context.StoreId,
            context.InvoiceId,
            proposal.CreatePostRequest,
            requestResponse => (new SystemUri(requestResponse.Request.Url, UriKind.Absolute), requestResponse.Request.ContentType, requestResponse.Request.Body),
            cancellationToken).ConfigureAwait(false);
        var responseBody = relayResponse.ResponseBody;
        var requestResponse = relayResponse.RequestContext;
        using var relayRequestContext = requestResponse;
        RequireCurrentContext(context);

        // TODO: Preserve POST error types in FFI and retry transient failures from the persisted proposal.
        // https://github.com/ValeraFinebits/btcpayserver-payjoin-plugin/issues/198
        using var transition = proposal.ProcessResponse(responseBody, requestResponse.ClientResponse);
        using var _ = transition.Save(context.Persister);
    }

    private void RequireCurrentContext(PayjoinReceiverProposalFinalizationContext context)
    {
        _sessionStore.RequireNegotiationAllowed(context.Persister);
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
        var matches = finalTransaction.Outputs
            .Select((output, index) => new ExpectedFinalOutput(index, output.Value.Satoshi, output.ScriptPubKey))
            .Where(output => output.ScriptPubKey == settlementScript).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private sealed record ExpectedFinalOutput(int Index, long ValueSats, Script ScriptPubKey);
}
