using BTCPayServer.Services.Wallets;
using Payjoin;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal interface IPayjoinReceiverProposalFinalizer
{
    Task FinalizeAsync(
        PayjoinReceiverProposalFinalizationContext context,
        WantsFeeRange proposal,
        ReceivedCoin[] receiverCoins,
        CancellationToken cancellationToken);

    Task FinalizeAsync(
        PayjoinReceiverProposalFinalizationContext context,
        ProvisionalProposal proposal,
        ReceivedCoin[] receiverCoins,
        CancellationToken cancellationToken);

    // Takes the generated proposal interface rather than the concrete FFI object so the
    // recording step can be exercised in tests, where native handles cannot be constructed.
    Task EnsureExpectedFinalTransactionAsync(
        PayjoinReceiverProposalFinalizationContext context,
        IPayjoinProposal proposal,
        CancellationToken cancellationToken);

    Task PostAsync(
        PayjoinReceiverProposalFinalizationContext context,
        IPayjoinProposal proposal,
        CancellationToken cancellationToken);
}

internal sealed class PayjoinReceiverProposalFinalizationContext
{
    public PayjoinReceiverProposalFinalizationContext(
        JsonReceiverSessionPersister persister,
        string storeId,
        string invoiceId,
        string cryptoCode)
    {
        Persister = persister;
        StoreId = storeId;
        InvoiceId = invoiceId;
        CryptoCode = cryptoCode;
    }

    internal JsonReceiverSessionPersister Persister { get; }

    internal string StoreId { get; }

    internal string InvoiceId { get; }

    internal string CryptoCode { get; }
}
