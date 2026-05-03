using BTCPayServer.Services.Invoices;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

public sealed class PayjoinInvoiceLookup : IPayjoinInvoiceLookup
{
    private readonly InvoiceRepository _invoiceRepository;

    public PayjoinInvoiceLookup(InvoiceRepository invoiceRepository)
    {
        _invoiceRepository = invoiceRepository;
    }

    public Task<InvoiceEntity?> GetInvoiceAsync(string invoiceId)
    {
        return _invoiceRepository.GetInvoice(invoiceId);
    }
}
