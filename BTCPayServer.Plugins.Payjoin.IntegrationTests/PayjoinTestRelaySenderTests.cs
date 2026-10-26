using BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;
using System.Net;
using System.Security.Authentication;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests;

public sealed class PayjoinTestRelaySenderTests
{
    private static readonly Uri HealthyRelay = new("https://healthy.example.test/");
    private static readonly Uri ExpiredRelay = new("https://expired.example.test/");

    [Fact]
    public async Task PollRetriesTimeoutAfterFailingOverPastExpiredCertificate()
    {
        var contexts = new List<TestContext>();
        var healthyAttempts = 0;
        var sender = new PayjoinTestRelaySender("payer", TimeSpan.Zero);
        var (response, successfulContext) = await sender.SendAsync(
            [HealthyRelay, ExpiredRelay],
            relay => CreateContext(contexts, relay),
            (context, _) =>
            {
                if (context.Relay == ExpiredRelay)
                {
                    throw new HttpRequestException("TLS failed", new AuthenticationException("NotTimeValid"));
                }
                if (++healthyAttempts == 1)
                {
                    throw new HttpRequestException("Long poll timed out", new OperationCanceledException());
                }
                return Task.FromResult(new byte[] { 42 });
            },
            retryTransientFailures: true, Xunit.TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(new byte[] { 42 }, response);
        Assert.Equal(3, contexts.Count);
        Assert.All(contexts.Take(2), context => Assert.Equal(1, context.DisposeCount));
        Assert.Same(contexts[2], successfulContext);
        Assert.Equal(0, successfulContext.DisposeCount);
        successfulContext.Dispose();
    }

    [Fact]
    public async Task CallerCancellationStopsRetriesAndPreservesRelayErrors()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Xunit.TestContext.Current.CancellationToken);
        var contexts = new List<TestContext>();
        var attempts = 0;
        var sender = new PayjoinTestRelaySender("payer", TimeSpan.Zero);
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => sender.SendAsync(
            [HealthyRelay], relay => CreateContext(contexts, relay),
            (_, token) =>
            {
                if (++attempts == 2)
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }
                throw new HttpRequestException("Long poll timed out");
            },
            retryTransientFailures: true, cancellation.Token)).ConfigureAwait(true);

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Contains(HealthyRelay.AbsoluteUri, exception.Message, StringComparison.Ordinal);
        Assert.Contains("Long poll timed out", exception.Message, StringComparison.Ordinal);
        Assert.IsType<AggregateException>(exception.InnerException);
        Assert.Equal(2, attempts);
        Assert.All(contexts, context => Assert.Equal(1, context.DisposeCount));
    }

    [Fact]
    public async Task CancellationTakesPrecedenceOverTheLastRelayFailure()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Xunit.TestContext.Current.CancellationToken);
        var contexts = new List<TestContext>();
        var sender = new PayjoinTestRelaySender("payer", TimeSpan.Zero);
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => sender.SendAsync(
            [HealthyRelay], relay => CreateContext(contexts, relay),
            (_, _) =>
            {
                cancellation.Cancel();
                throw new HttpRequestException("Post timed out");
            },
            retryTransientFailures: false, cancellation.Token)).ConfigureAwait(true);

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Contains("Post timed out", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, Assert.Single(contexts).DisposeCount);
    }

    [Fact]
    public async Task PermanentFailuresStopWithoutRetryAndReportEveryRelay()
    {
        var contexts = new List<TestContext>();
        var sender = new PayjoinTestRelaySender("payer", TimeSpan.Zero);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync(
            [HealthyRelay, ExpiredRelay], relay => CreateContext(contexts, relay),
            (context, _) => throw (context.Relay == ExpiredRelay
                ? new HttpRequestException("TLS failed", new AuthenticationException("NotTimeValid"))
                : new HttpRequestException("Bad relay request", null, HttpStatusCode.BadRequest)),
            retryTransientFailures: true, Xunit.TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Contains(HealthyRelay.AbsoluteUri, exception.Message, StringComparison.Ordinal);
        Assert.Contains(ExpiredRelay.AbsoluteUri, exception.Message, StringComparison.Ordinal);
        Assert.Contains("Bad relay request", exception.Message, StringComparison.Ordinal);
        Assert.Contains("TLS failed", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, Assert.IsType<AggregateException>(exception.InnerException).InnerExceptions.Count);
        Assert.Equal(2, contexts.Count);
        Assert.All(contexts, context => Assert.Equal(1, context.DisposeCount));
    }

    [Fact]
    public async Task OriginalPostDoesNotRetryTransientFailures()
    {
        var contexts = new List<TestContext>();
        var sender = new PayjoinTestRelaySender("payer", TimeSpan.Zero);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync(
            [HealthyRelay], relay => CreateContext(contexts, relay),
            (_, _) => throw new HttpRequestException("Post timed out"),
            retryTransientFailures: false, Xunit.TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Contains("Post timed out", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, Assert.Single(contexts).DisposeCount);
    }

    [Fact]
    public async Task ProgrammingErrorPropagatesWithoutFailoverAndDisposesContext()
    {
        var contexts = new List<TestContext>();
        var sender = new PayjoinTestRelaySender("payer", TimeSpan.Zero);
        var failure = new InvalidOperationException("Invalid request context");
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync(
            [HealthyRelay, ExpiredRelay], relay => CreateContext(contexts, relay),
            (_, _) => throw failure,
            retryTransientFailures: true, Xunit.TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Same(failure, exception);
        Assert.Equal(1, Assert.Single(contexts).DisposeCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task PollRetriesTransientHttpStatus(HttpStatusCode status)
    {
        var contexts = new List<TestContext>();
        var sender = new PayjoinTestRelaySender("payer", TimeSpan.Zero);
        var attempts = 0;
        var (_, successfulContext) = await sender.SendAsync(
            [HealthyRelay], relay => CreateContext(contexts, relay),
            (_, _) => ++attempts == 1
                ? throw new HttpRequestException("Temporary relay failure", null, status)
                : Task.FromResult(Array.Empty<byte>()),
            retryTransientFailures: true, Xunit.TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(2, attempts);
        Assert.Equal(1, contexts[0].DisposeCount);
        Assert.Equal(0, successfulContext.DisposeCount);
        successfulContext.Dispose();
    }

    private static TestContext CreateContext(List<TestContext> contexts, Uri relay)
    {
        var context = new TestContext(relay);
        contexts.Add(context);
        return context;
    }

    private sealed class TestContext(Uri relay) : IDisposable
    {
        public Uri Relay { get; } = relay;
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }
}
