using BTCPayServer.Services.Invoices;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

public interface IPayjoinInvoiceLookup
{
    Task<InvoiceEntity?> GetInvoiceAsync(string invoiceId);
}
