using BTCPayServer.Services.Invoices;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal interface IPayjoinInvoiceEventWriter
{
    Task WriteAsync(PayjoinInvoiceEvent invoiceEvent);
}

internal sealed class PayjoinInvoiceEventWriter : IPayjoinInvoiceEventWriter
{
    private readonly InvoiceRepository _invoiceRepository;

    public PayjoinInvoiceEventWriter(InvoiceRepository invoiceRepository)
    {
        _invoiceRepository = invoiceRepository;
    }

    public Task WriteAsync(PayjoinInvoiceEvent invoiceEvent) =>
        _invoiceRepository.AddInvoiceLogs(invoiceEvent.InvoiceId, invoiceEvent.Logs);
}
