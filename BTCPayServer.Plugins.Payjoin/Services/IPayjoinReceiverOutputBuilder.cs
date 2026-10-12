using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal interface IPayjoinReceiverOutputBuilder
{
    Task<PayjoinReceiverOutputBuilder.OutputBuildResult> TryCreateSettlementOutputsAsync(
        string storeId,
        string invoiceId,
        byte[] receiverScript,
        bool preserveReceiverScript,
        long? pinnedSettlementAmountSats,
        CancellationToken cancellationToken);
}
