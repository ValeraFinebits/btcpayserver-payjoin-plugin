using BTCPayServer.Plugins.Payjoin.Services;
using NSubstitute;
using System.Net;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinReceiverRelayClientTests
{
    [Fact]
    public async Task SendAsyncPostsRequestBodyAndReturnsResponseBody()
    {
        // Arrange
        var cancellationToken = global::Xunit.TestContext.Current.CancellationToken;
        var expectedResponseBody = new byte[] { 0xCA, 0xFE };
        using var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(expectedResponseBody)
        });
        using var httpClient = new HttpClient(handler);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(nameof(PayjoinReceiverPoller)).Returns(httpClient);
        var relayClient = new PayjoinReceiverRelayClient(httpClientFactory);
        var url = new Uri("https://relay.example");
        const string contentType = "application/http";
        var body = new byte[] { 0x01, 0x02, 0x03 };

        // Act
        var responseBody = await relayClient.SendAsync(url, contentType, body, cancellationToken);

        // Assert
        Assert.Equal(expectedResponseBody, responseBody);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal(url.ToString(), handler.LastRequest.RequestUri!.ToString());
        Assert.Equal(contentType, handler.LastContentType);
        Assert.Equal(body, handler.LastBody);
    }

    [Fact]
    public async Task SendAsyncThrowsRelayTimeoutWhenLocalTimeoutFires()
    {
        // Arrange
        var timeout = TimeSpan.FromMilliseconds(10);
        using var handler = new DelayingHandler();
        using var httpClient = new HttpClient(handler);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(nameof(PayjoinReceiverPoller)).Returns(httpClient);
        var relayClient = new PayjoinReceiverRelayClient(httpClientFactory, timeout);

        // Act
        var exception = await Assert.ThrowsAsync<PayjoinReceiverRelayTimeoutException>(() =>
            relayClient.SendAsync(new Uri("https://relay.example"), "application/http", Array.Empty<byte>(), CancellationToken.None));

        // Assert
        Assert.Equal(timeout, exception.Timeout);
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory = responseFactory;

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastContentType { get; private set; }
        public byte[]? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastContentType = request.Content?.Headers.ContentType?.ToString();
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return _responseFactory(request);
        }
    }

    private sealed class DelayingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = request;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
