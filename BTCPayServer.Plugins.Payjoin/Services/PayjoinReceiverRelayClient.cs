using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using SystemUri = System.Uri;

namespace BTCPayServer.Plugins.Payjoin.Services;

public sealed class PayjoinReceiverRelayClient : IPayjoinReceiverRelayClient
{
    private static readonly TimeSpan RelayRequestTimeout = TimeSpan.FromSeconds(10);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeSpan _relayRequestTimeout;

    public PayjoinReceiverRelayClient(IHttpClientFactory httpClientFactory)
        : this(httpClientFactory, RelayRequestTimeout)
    {
    }

    internal PayjoinReceiverRelayClient(IHttpClientFactory httpClientFactory, TimeSpan relayRequestTimeout)
    {
        _httpClientFactory = httpClientFactory;
        _relayRequestTimeout = relayRequestTimeout;
    }

    public async Task<byte[]> SendAsync(SystemUri url, string contentType, byte[] body, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(body)
        };
        message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

        var client = _httpClientFactory.CreateClient(nameof(PayjoinReceiverPoller));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_relayRequestTimeout);
        try
        {
            using var response = await client.SendAsync(message, timeoutCts.Token).ConfigureAwait(false);
            return await response.Content.ReadAsByteArrayAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (IsExpectedRelayTimeout(ex, timeoutCts.Token, cancellationToken))
        {
            throw new PayjoinReceiverRelayTimeoutException(_relayRequestTimeout, ex);
        }
    }

    internal static bool IsExpectedRelayTimeout(
        OperationCanceledException exception,
        CancellationToken timeoutToken,
        CancellationToken callerToken)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return timeoutToken.IsCancellationRequested && !callerToken.IsCancellationRequested;
    }
}
