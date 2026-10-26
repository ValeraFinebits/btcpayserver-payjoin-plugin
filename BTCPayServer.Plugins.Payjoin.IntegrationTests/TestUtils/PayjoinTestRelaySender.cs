using System.Net;
using System.Security.Authentication;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;

internal sealed class PayjoinTestRelaySender
{
    private readonly string _payerStoreId;
    private readonly TimeSpan _retryDelay;

    public PayjoinTestRelaySender(string payerStoreId, TimeSpan retryDelay)
    {
        ArgumentException.ThrowIfNullOrEmpty(payerStoreId);
        if (retryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryDelay));
        }

        _payerStoreId = payerStoreId;
        _retryDelay = retryDelay;
    }

    public async Task<(byte[] Response, TContext Context)> SendAsync<TContext>(
        IReadOnlyList<Uri> relayUrls,
        Func<Uri, TContext> createContext,
        Func<TContext, CancellationToken, Task<byte[]>> sendRequest,
        bool retryTransientFailures,
        CancellationToken cancellationToken)
        where TContext : IDisposable
    {
        ArgumentNullException.ThrowIfNull(relayUrls);
        ArgumentNullException.ThrowIfNull(createContext);
        ArgumentNullException.ThrowIfNull(sendRequest);
        if (relayUrls.Count == 0)
        {
            throw new InvalidOperationException($"No OHTTP relays configured for payer store '{_payerStoreId}'.");
        }

        var failures = new Dictionary<Uri, (Exception Error, bool IsTransient)>();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var relayUrl in relayUrls)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (failures.TryGetValue(relayUrl, out var previous) && !previous.IsTransient)
                    {
                        continue;
                    }

                    var context = createContext(relayUrl);
                    try
                    {
                        var response = await sendRequest(context, cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        return (response, context);
                    }
                    catch (HttpRequestException ex)
                    {
                        context.Dispose();
                        failures[relayUrl] = (ex, IsTransient(ex));
                    }
                    catch
                    {
                        context.Dispose();
                        throw;
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (!retryTransientFailures || failures.Values.All(failure => !failure.IsTransient))
                {
                    throw new InvalidOperationException($"All configured OHTTP relays failed. {FormatFailures(failures)}", AggregateFailures(failures));
                }

                await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && failures.Count > 0)
        {
            throw new OperationCanceledException(
                $"OHTTP relay polling canceled. {FormatFailures(failures)}",
                AggregateFailures(failures), cancellationToken);
        }
    }

    private static bool IsTransient(HttpRequestException exception)
    {
        for (Exception? cause = exception; cause is not null; cause = cause.InnerException)
        {
            if (cause is AuthenticationException)
            {
                return false;
            }
        }

        if (exception.StatusCode is not { } statusCode)
        {
            return true;
        }

        return statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
            (int)statusCode is >= 500 and <= 599;
    }

    private string FormatFailures(Dictionary<Uri, (Exception Error, bool IsTransient)> failures)
    {
        var details = failures.Select(failure =>
            $"RelayUrl='{failure.Key}', Error='{failure.Value.Error.Message}'");
        return $"Relay failures for payer store '{_payerStoreId}'. {string.Join("; ", details)}";
    }

    private static AggregateException AggregateFailures(Dictionary<Uri, (Exception Error, bool IsTransient)> failures)
    {
        return new AggregateException(failures.Select(failure =>
            new InvalidOperationException($"OHTTP relay '{failure.Key}' failed.", failure.Value.Error)));
    }
}
