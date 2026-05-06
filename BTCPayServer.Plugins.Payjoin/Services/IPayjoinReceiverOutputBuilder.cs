using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal interface IPayjoinReceiverOutputBuilder
{
    Task<PayjoinReceiverOutputBuilder.OutputReplacement?> TryCreateExactPaymentOutputsAsync(
        string storeId,
        string invoiceId,
        byte[] receiverScript,
        CancellationToken cancellationToken);
}
