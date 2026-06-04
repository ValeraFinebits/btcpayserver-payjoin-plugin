using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal interface IPayjoinReceiverOutputBuilder
{
    Task<PayjoinReceiverOutputBuilder.OutputReplacement?> TryCreateSettlementOutputsAsync(
        string storeId,
        string invoiceId,
        byte[] receiverScript,
        bool preserveReceiverScript,
        CancellationToken cancellationToken);
}
