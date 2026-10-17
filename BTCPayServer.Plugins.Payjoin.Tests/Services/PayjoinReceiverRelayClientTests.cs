using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.Net;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinReceiverRelayClientTests
{
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task SendAsyncRejectsUnsuccessfulHttpStatusBeforeReturningTheBody(HttpStatusCode status)
    {
        using var handler = new CapturingHandler(_ => new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent([])
        });
        using var httpClient = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(nameof(PayjoinReceiverPoller)).Returns(httpClient);
        var client = new PayjoinReceiverRelayClient(factory);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.SendAsync(new Uri("https://relay.example/"), "application/http", [], CancellationToken.None));

        Assert.Equal(status, exception.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task SendAsyncKeepsRejectionSessionScopedWhenErrorBodyWouldFail(HttpStatusCode status)
    {
        var relay = new Uri("https://relay.example/");
        using var content = new FailingContent();
        using var handler = new CapturingHandler(_ => new HttpResponseMessage(status) { Content = content });
        using var httpClient = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(nameof(PayjoinReceiverPoller)).Returns(httpClient);
        var settings = Substitute.For<IPayjoinStoreSettingsRepository>();
        settings.GetAsync("store").Returns(Task.FromResult<PayjoinStoreSettings?>(new PayjoinStoreSettings { OhttpRelayUrls = [relay] }));
        var manager = new PayjoinMailroomManager(NullLogger<PayjoinMailroomManager>.Instance, TimeSpan.FromMinutes(10),
            (_, _, _, _) => throw new NotSupportedException());
        var sender = new PayjoinReceiverRelayRequestSender(settings, manager, new PayjoinReceiverRelayClient(factory));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => sender.SendAsync("store", "invoice-1",
            url => new HttpRequestMessage(HttpMethod.Post, url),
            request => (request.RequestUri!, "application/http", Array.Empty<byte>()), CancellationToken.None));

        Assert.Equal(status, exception.StatusCode);
        Assert.Equal(0, content.ReadAttempts);
        Assert.Null(manager.ChooseRelayForRequest([relay], "invoice-1"));
        Assert.Equal(relay, manager.ChooseRelayForRequest([relay], "invoice-2"));
    }

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

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(PayjoinReceiverRelayClient.MaxResponseBodySize - 1, false)]
    [InlineData(PayjoinReceiverRelayClient.MaxResponseBodySize - 1, true)]
    [InlineData(PayjoinReceiverRelayClient.MaxResponseBodySize, false)]
    [InlineData(PayjoinReceiverRelayClient.MaxResponseBodySize, true)]
    public async Task SendAsyncReturnsBodiesWithinProtocolLimit(int bodySize, bool hasContentLength)
    {
        using var stream = new FiniteResponseStream(bodySize);
        using var content = new StreamContent(stream);
        if (hasContentLength)
        {
            content.Headers.ContentLength = bodySize;
        }
        using var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        using var httpClient = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(nameof(PayjoinReceiverPoller)).Returns(httpClient);
        var client = new PayjoinReceiverRelayClient(factory);

        var body = await client.SendAsync(new Uri("https://relay.example/"), "application/http", [], CancellationToken.None);

        Assert.Equal(Enumerable.Repeat((byte)0x42, bodySize), body);
        Assert.Equal(bodySize, stream.BytesRead);
        Assert.True(stream.Disposed);
    }

    [Theory]
    [InlineData(PayjoinReceiverRelayClient.MaxResponseBodySize + 1, false)]
    [InlineData(PayjoinReceiverRelayClient.MaxResponseBodySize + 1, true)]
    [InlineData(65536, false)]
    [InlineData(65536, true)]
    [InlineData(16 * 1024 * 1024, false)]
    [InlineData(16 * 1024 * 1024, true)]
    public async Task SendAsyncRejectsOversizedBodiesWithoutReadingTheRest(int bodySize, bool hasContentLength)
    {
        using var stream = new FiniteResponseStream(bodySize);
        using var content = new StreamContent(stream);
        if (hasContentLength)
        {
            content.Headers.ContentLength = bodySize;
        }
        using var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        using var httpClient = new HttpClient(handler) { MaxResponseContentBufferSize = PayjoinReceiverRelayClient.MaxResponseBodySize };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(nameof(PayjoinReceiverPoller)).Returns(httpClient);
        var client = new PayjoinReceiverRelayClient(factory);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.SendAsync(new Uri("https://relay.example/"), "application/http", [], CancellationToken.None));

        Assert.Null(exception.StatusCode);
        if (hasContentLength)
        {
            Assert.Equal(0, stream.BytesRead);
        }
        else
        {
            Assert.InRange(stream.BytesRead, PayjoinReceiverRelayClient.MaxResponseBodySize + 1, PayjoinReceiverRelayClient.MaxResponseBodySize + FiniteResponseStream.ChunkSize);
        }
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task SendAsyncBoundsReadingEvenWhenContentLengthUnderstatesBodySize()
    {
        using var stream = new FiniteResponseStream(65536);
        using var content = new StreamContent(stream);
        content.Headers.ContentLength = PayjoinReceiverRelayClient.MaxResponseBodySize;
        using var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        using var httpClient = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(nameof(PayjoinReceiverPoller)).Returns(httpClient);
        var client = new PayjoinReceiverRelayClient(factory);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.SendAsync(new Uri("https://relay.example/"), "application/http", [], CancellationToken.None));

        Assert.Null(exception.StatusCode);
        Assert.InRange(stream.BytesRead, PayjoinReceiverRelayClient.MaxResponseBodySize + 1, PayjoinReceiverRelayClient.MaxResponseBodySize + FiniteResponseStream.ChunkSize);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task SendAsyncRetriesAnotherRelayAfterOversizedResponse()
    {
        var relays = new[] { new Uri("https://relay-1.example/"), new Uri("https://relay-2.example/") };
        using var oversizedStream = new FiniteResponseStream(65536);
        using var oversizedContent = new StreamContent(oversizedStream);
        Uri? failedRelay = null;
        using var handler = new CapturingHandler(request =>
        {
            if (failedRelay is null)
            {
                failedRelay = request.RequestUri;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = oversizedContent };
            }

            Assert.NotEqual(failedRelay, request.RequestUri);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0xCA, 0xFE]) };
        });
        using var httpClient = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(nameof(PayjoinReceiverPoller)).Returns(httpClient);
        var settings = Substitute.For<IPayjoinStoreSettingsRepository>();
        settings.GetAsync("store").Returns(Task.FromResult<PayjoinStoreSettings?>(new PayjoinStoreSettings { OhttpRelayUrls = relays }));
        var manager = new PayjoinMailroomManager(NullLogger<PayjoinMailroomManager>.Instance, TimeSpan.FromMinutes(10),
            (_, _, _, _) => throw new NotSupportedException());
        var sender = new PayjoinReceiverRelayRequestSender(settings, manager, new PayjoinReceiverRelayClient(factory));
        var contexts = new List<HttpRequestMessage>();

        var result = await sender.SendAsync("store", "invoice", relay =>
        {
            var context = new HttpRequestMessage(HttpMethod.Post, relay);
            contexts.Add(context);
            return context;
        }, request => (request.RequestUri!, "application/http", Array.Empty<byte>()), CancellationToken.None);
        using var successfulContext = result.RequestContext;

        Assert.Equal(new byte[] { 0xCA, 0xFE }, result.ResponseBody);
        Assert.Equal(2, contexts.Count);
        Assert.Throws<ObjectDisposedException>(() => contexts[0].Method = HttpMethod.Get);
        Assert.True(oversizedStream.Disposed);
        Assert.InRange(oversizedStream.BytesRead, PayjoinReceiverRelayClient.MaxResponseBodySize + 1, PayjoinReceiverRelayClient.MaxResponseBodySize + FiniteResponseStream.ChunkSize);
        Assert.Null(manager.ChooseRelayForRequest([failedRelay!], "another-invoice"));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendAsyncCancelsStalledBodyAfterSuccessfulHeaders(bool callerCancels)
    {
        using var content = new BlockingContent();
        using var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(nameof(PayjoinReceiverPoller)).Returns(httpClient);
        var timeout = callerCancels ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(500);
        var client = new PayjoinReceiverRelayClient(factory, timeout);
        using var cancellation = new CancellationTokenSource();
        var testCancellation = global::Xunit.TestContext.Current.CancellationToken;
        var sending = client.SendAsync(new Uri("https://relay.example/"), "application/http", [], cancellation.Token);

        try
        {
            await content.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), testCancellation);
            if (callerCancels)
            {
                Assert.False(sending.IsCompleted);
                cancellation.Cancel();
                var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    sending.WaitAsync(TimeSpan.FromSeconds(5), testCancellation));
                Assert.IsNotType<PayjoinReceiverRelayTimeoutException>(exception);
            }
            else
            {
                var exception = await Assert.ThrowsAsync<PayjoinReceiverRelayTimeoutException>(() =>
                    sending.WaitAsync(TimeSpan.FromSeconds(5), testCancellation));
                Assert.Equal(timeout, exception.Timeout);
            }
        }
        finally
        {
            content.ReleaseRead();
            try
            {
                await sending.ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
        }
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

    private sealed class FailingContent : HttpContent
    {
        public int ReadAttempts { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            ReadAttempts++;
            return Task.FromException(new IOException("Injected connection reset while reading the rejection body."));
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class FiniteResponseStream(int length) : Stream
    {
        public const int ChunkSize = 1024;
        public int BytesRead { get; private set; }
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var count = Math.Min(length - BytesRead, Math.Min(buffer.Length, ChunkSize));
            buffer[..count].Fill(0x42);
            BytesRead += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class BlockingContent : HttpContent
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseRead() => _release.TrySetResult();

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            var reading = _release.Task.WaitAsync(cancellationToken);
            ReadStarted.TrySetResult();
            return reading;
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
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
