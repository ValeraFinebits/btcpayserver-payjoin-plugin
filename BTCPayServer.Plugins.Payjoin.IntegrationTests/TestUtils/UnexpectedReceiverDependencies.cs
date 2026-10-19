using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Wallets;
using Payjoin;
using System.Collections.Concurrent;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;

internal sealed class UnexpectedReceiverDependencies : IPayjoinReceiverRelayRequestSender,
    IPayjoinWalletOwnershipService, IPayjoinReceiverOutputBuilder, IPayjoinReceiverInputSelector,
    IPayjoinAccountingPaymentService, IPayjoinInvoiceLookup, IPayjoinReceiverProposalFinalizer
{
    public ConcurrentQueue<string> Calls { get; } = new();

    public Task<(byte[] ResponseBody, TRequestContext RequestContext)> SendAsync<TRequestContext>(
        string storeId, string invoiceId, Func<string, TRequestContext> buildRequest,
        Func<TRequestContext, (System.Uri Url, string ContentType, byte[] Body)> describeRequest,
        CancellationToken cancellationToken) where TRequestContext : IDisposable
        => Unexpected<Task<(byte[], TRequestContext)>>(nameof(SendAsync));

    public Task<PayjoinInputOwnershipResolver> CreateInputResolverAsync(string storeId,
        IReadOnlyCollection<NBitcoin.OutPoint> candidateInputOutpoints, CancellationToken cancellationToken)
        => Unexpected<Task<PayjoinInputOwnershipResolver>>(nameof(CreateInputResolverAsync));

    public Task<PayjoinOwnershipLookupResult> CreateResolverAsync(PayjoinReceiverSessionState session,
        PayjoinOriginalTransactionFacts original, byte[] receiverScript, CancellationToken cancellationToken)
        => Unexpected<Task<PayjoinOwnershipLookupResult>>(nameof(CreateResolverAsync));

    public Task<PayjoinReceiverOutputBuilder.OutputBuildResult> TryCreateSettlementOutputsAsync(string storeId,
        string invoiceId, byte[] receiverScript, bool preserveReceiverScript, long? pinnedSettlementAmountSats,
        CancellationToken cancellationToken)
        => Unexpected<Task<PayjoinReceiverOutputBuilder.OutputBuildResult>>(nameof(TryCreateSettlementOutputsAsync));

    public Task<ReceiverInputContributionResult> TryContributeInputsAsync(WantsInputs proposal, string storeId,
        DateTimeOffset reservationExpiresAt, PayjoinReceiverSessionStore.DatabaseBackedReceiverPersister owner,
        CancellationToken cancellationToken)
        => Unexpected<Task<ReceiverInputContributionResult>>(nameof(TryContributeInputsAsync));

    public Task<ReceivedCoin[]?> TryGetPersistedContributedCoinsAsync(PayjoinReceiverSessionState session,
        CancellationToken cancellationToken)
        => Unexpected<Task<ReceivedCoin[]?>>(nameof(TryGetPersistedContributedCoinsAsync));

    public Task<PaymentEntity?> ReconcileWithFinalTransactionAsync(PayjoinAccountingBridgeState bridge,
        CancellationToken cancellationToken)
        => Unexpected<Task<PaymentEntity?>>(nameof(ReconcileWithFinalTransactionAsync));

    public Task<InvoiceEntity?> GetInvoiceAsync(string invoiceId)
        => Unexpected<Task<InvoiceEntity?>>(nameof(GetInvoiceAsync));

    public Task FinalizeAsync(PayjoinReceiverProposalFinalizationContext context, WantsFeeRange proposal,
        ReceivedCoin[] receiverCoins, CancellationToken cancellationToken)
        => Unexpected<Task>(nameof(FinalizeAsync));

    public Task FinalizeAsync(PayjoinReceiverProposalFinalizationContext context, ProvisionalProposal proposal,
        ReceivedCoin[] receiverCoins, CancellationToken cancellationToken)
        => Unexpected<Task>(nameof(FinalizeAsync));

    public Task EnsureExpectedFinalTransactionAsync(PayjoinReceiverProposalFinalizationContext context,
        IPayjoinProposal proposal, CancellationToken cancellationToken)
        => Unexpected<Task>(nameof(EnsureExpectedFinalTransactionAsync));

    public Task PostAsync(PayjoinReceiverProposalFinalizationContext context, IPayjoinProposal proposal,
        CancellationToken cancellationToken)
        => Unexpected<Task>(nameof(PostAsync));

    public Task<PayjoinOhttpKeysFetchResult> FetchKeysAsync(System.Uri relay, string storeId, string invoiceId,
        CancellationToken cancellationToken)
        => Unexpected<Task<PayjoinOhttpKeysFetchResult>>(nameof(FetchKeysAsync));

    private T Unexpected<T>(string method)
    {
        Calls.Enqueue(method);
        throw new InvalidOperationException($"Unexpected receiver dependency call: {method}.");
    }
}
