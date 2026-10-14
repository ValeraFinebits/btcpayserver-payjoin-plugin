using BTCPayServer.Plugins.Payjoin.Services;
using System.Collections.Concurrent;

namespace BTCPayServer.Plugins.Payjoin.Tests;

internal sealed class RecordingInvoiceEventWriter(Func<PayjoinInvoiceEvent, Task>? write = null) : IPayjoinInvoiceEventWriter
{
    public ConcurrentQueue<PayjoinInvoiceEvent> Attempts { get; } = new();
    public ConcurrentQueue<PayjoinInvoiceEvent> Writes { get; } = new();

    public async Task WriteAsync(PayjoinInvoiceEvent invoiceEvent)
    {
        Attempts.Enqueue(invoiceEvent);
        if (write is not null)
        {
            await write(invoiceEvent).ConfigureAwait(false);
        }
        Writes.Enqueue(invoiceEvent);
    }
}
