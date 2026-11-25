using BTCPayServer.Plugins.Payjoin.Models;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

public sealed class PayjoinCheckoutPaymentUrl
{
    internal const int MaxConcurrentInvoices = 8;
    internal const int MaxNewInvoicesPerWindow = 16;
    internal static readonly TimeSpan NewInvoiceWindow = TimeSpan.FromSeconds(1);

    private readonly IPayjoinInvoicePaymentUrlService _paymentUrls;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly TimeProvider _clock;
    private readonly object _sync = new();
    private readonly Dictionary<string, Task<GetBip21Response?>> _pending = new(StringComparer.Ordinal);
    private long _windowStarted;
    private int _windowStarts;

    public PayjoinCheckoutPaymentUrl(IPayjoinInvoicePaymentUrlService paymentUrls, IHostApplicationLifetime lifetime)
        : this(paymentUrls, lifetime, TimeProvider.System)
    {
    }

    internal PayjoinCheckoutPaymentUrl(
        IPayjoinInvoicePaymentUrlService paymentUrls,
        IHostApplicationLifetime lifetime,
        TimeProvider clock)
    {
        _paymentUrls = paymentUrls;
        _lifetime = lifetime;
        _clock = clock;
        _windowStarted = clock.GetTimestamp();
    }

    // A null task means the server is at capacity. A null result means the invoice has no payment URL.
    internal Task<GetBip21Response?>? TryGetPaymentUrl(string invoiceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource<GetBip21Response?> completion;
        lock (_sync)
        {
            if (_pending.TryGetValue(invoiceId, out var pending))
            {
                return pending.WaitAsync(cancellationToken);
            }

            if (_pending.Count >= MaxConcurrentInvoices)
            {
                return null;
            }

            if (!TryStartNewInvoice())
            {
                return null;
            }

            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Add(invoiceId, completion.Task);
        }

        _ = GeneratePaymentUrlAsync(invoiceId, completion);
        return completion.Task.WaitAsync(cancellationToken);
    }

    private async Task GeneratePaymentUrlAsync(string invoiceId, TaskCompletionSource<GetBip21Response?> completion)
    {
        var generation = GenerateAsync();
        await ((Task)generation).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        lock (_sync)
        {
            _pending.Remove(invoiceId);
            completion.SetFromTask(generation);
        }

        // Observe faults even if all checkout callers canceled their waits.
        _ = completion.Task.Exception;

        // A checkout disconnect cancels its own wait. Other callers still need the result.
        async Task<GetBip21Response?> GenerateAsync() =>
            await _paymentUrls.GetInvoicePaymentUrlAsync(invoiceId, _lifetime.ApplicationStopping).ConfigureAwait(false);
    }

    // Fixed one-second windows of new generation starts. Callers joining pending work do not count.
    private bool TryStartNewInvoice()
    {
        var now = _clock.GetTimestamp();
        if (_clock.GetElapsedTime(_windowStarted, now) >= NewInvoiceWindow)
        {
            _windowStarted = now;
            _windowStarts = 0;
        }

        if (_windowStarts >= MaxNewInvoicesPerWindow)
        {
            return false;
        }

        _windowStarts++;
        return true;
    }
}
