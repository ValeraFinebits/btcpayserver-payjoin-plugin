using BTCPayServer.Plugins.Payjoin.Models;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

public interface IPayjoinInvoicePaymentUrlService
{
    Task<GetBip21Response?> GetInvoicePaymentUrlAsync(string invoiceId, CancellationToken cancellationToken = default);
}
