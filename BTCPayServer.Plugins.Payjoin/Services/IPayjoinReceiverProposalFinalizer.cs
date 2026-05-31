using BTCPayServer.Services.Wallets;
using Payjoin;
using System.Threading;
using System.Threading.Tasks;
using SystemUri = System.Uri;

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

    Task PostAsync(
        PayjoinReceiverProposalFinalizationContext context,
        PayjoinProposal proposal,
        CancellationToken cancellationToken);
}

internal sealed class PayjoinReceiverProposalFinalizationContext
{
    public PayjoinReceiverProposalFinalizationContext(
        JsonReceiverSessionPersister persister,
        SystemUri ohttpRelayUrl,
        string storeId,
        string invoiceId,
        string cryptoCode)
    {
        Persister = persister;
        OhttpRelayUrl = ohttpRelayUrl;
        StoreId = storeId;
        InvoiceId = invoiceId;
        CryptoCode = cryptoCode;
    }

    internal JsonReceiverSessionPersister Persister { get; }

    internal SystemUri OhttpRelayUrl { get; }

    internal string StoreId { get; }

    internal string InvoiceId { get; }

    internal string CryptoCode { get; }
}
