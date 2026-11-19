using BTCPayServer.Plugins.Payjoin.Models;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

public sealed class PayjoinCheckoutPaymentUrl(
    IPayjoinInvoicePaymentUrlService paymentUrls,
    IHostApplicationLifetime lifetime) : IDisposable
{
    internal const int MaxConcurrentInvoices = 8;

    private readonly object _sync = new();
    private readonly Dictionary<string, Task<GetBip21Response?>> _pending = new(StringComparer.Ordinal);
    private readonly FixedWindowRateLimiter _starts = new(new FixedWindowRateLimiterOptions
    {
        AutoReplenishment = false,
        PermitLimit = 16,
        QueueLimit = 0,
        Window = TimeSpan.FromSeconds(1)
    });

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

            _starts.TryReplenish();
            using var limit = _starts.AttemptAcquire();
            if (!limit.IsAcquired)
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
            await paymentUrls.GetInvoicePaymentUrlAsync(invoiceId, lifetime.ApplicationStopping).ConfigureAwait(false);
    }

    public void Dispose() => _starts.Dispose();
}
