using BTCPayServer.Services.Wallets;
using Payjoin;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal interface IPayjoinReceiverProposalSigner
{
    Task<string> SignProposalAsync(
        ProvisionalProposal proposal,
        string storeId,
        ReceivedCoin[] receiverCoins,
        CancellationToken cancellationToken);
}
