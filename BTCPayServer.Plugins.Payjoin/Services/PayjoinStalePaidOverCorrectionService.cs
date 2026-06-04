using BTCPayServer.Client.Models;
using BTCPayServer.Services.Invoices;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal interface IPayjoinStalePaidOverCorrectionService
{
    Task ClearStalePaidOverAsync(string invoiceId);
}

internal sealed class PayjoinStalePaidOverCorrectionService : IPayjoinStalePaidOverCorrectionService
{
    private readonly InvoiceRepository _invoiceRepository;

    public PayjoinStalePaidOverCorrectionService(InvoiceRepository invoiceRepository)
    {
        _invoiceRepository = invoiceRepository;
    }

    public async Task ClearStalePaidOverAsync(string invoiceId)
    {
        var invoice = await _invoiceRepository.GetInvoice(invoiceId).ConfigureAwait(false);
        if (invoice is null)
        {
            return;
        }

        var state = invoice.GetInvoiceState();
        if (state.ExceptionStatus != InvoiceExceptionStatus.PaidOver)
        {
            return;
        }

        if (state.Status is not (InvoiceStatus.Processing or InvoiceStatus.Settled) || invoice.IsOverPaid)
        {
            return;
        }

        await _invoiceRepository.UpdateInvoiceStatus(invoiceId, state with { ExceptionStatus = InvoiceExceptionStatus.None }).ConfigureAwait(false);
    }
}