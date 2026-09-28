using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using SystemUri = System.Uri;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinReceiverRelayRequestSenderTests
{
    [Fact]
    public async Task SendAsyncRetriesWithAnotherRelayWhenTransportTimesOut()
    {
        var firstRelay = new SystemUri("https://relay-1.example/");
        var secondRelay = new SystemUri("https://relay-2.example/");
        var settingsRepository = Substitute.For<IPayjoinStoreSettingsRepository>();
        settingsRepository.GetAsync("store-1").Returns(Task.FromResult<PayjoinStoreSettings?>(new PayjoinStoreSettings
        {
            OhttpRelayUrls = [firstRelay, secondRelay]
        }));

        var relayClient = Substitute.For<IPayjoinReceiverRelayClient>();
        SystemUri? failedRelay = null;
        relayClient
            .SendAsync(Arg.Any<SystemUri>(), "application/http", Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var relayUrl = callInfo.ArgAt<SystemUri>(0);
                if (failedRelay is null)
                {
                    failedRelay = relayUrl;
                    return Task.FromException<byte[]>(new PayjoinReceiverRelayTimeoutException(TimeSpan.FromSeconds(5), new OperationCanceledException()));
                }

                Assert.NotEqual(failedRelay, relayUrl);
                return Task.FromResult(new byte[] { 0xCA, 0xFE });
            });

        var manager = new PayjoinMailroomManager(
            NullLogger<PayjoinMailroomManager>.Instance,
            TimeSpan.FromMinutes(10),
            (_, _, _, _) => Task.FromResult(PayjoinOhttpKeysFetchResult.RetryableFailure(new HttpRequestException("unused"))));
        var sender = new PayjoinReceiverRelayRequestSender(settingsRepository, manager, relayClient);
        var requestContexts = new List<TestRequestContext>();

        var (responseBody, requestContext) = await sender.SendAsync(
            "store-1",
            "invoice-1",
            relayUri =>
            {
                var context = new TestRequestContext(relayUri);
                requestContexts.Add(context);
                return context;
            },
            context => (new SystemUri(context.RelayUri), "application/http", [0x01, 0x02]),
            CancellationToken.None);

        Assert.Equal(new byte[] { 0xCA, 0xFE }, responseBody);
        Assert.Equal(2, requestContexts.Count);
        Assert.NotNull(failedRelay);
        Assert.NotEqual(requestContexts[0].RelayUri, requestContext.RelayUri);
        Assert.Equal(requestContext.RelayUri, requestContexts[1].RelayUri);
        Assert.True(requestContexts[0].Disposed);
        Assert.False(requestContexts[1].Disposed);
    }

    [Fact]
    public async Task SendAsyncThrowsWhenNoRelayUrlsAreConfigured()
    {
        var settingsRepository = Substitute.For<IPayjoinStoreSettingsRepository>();
        settingsRepository.GetAsync("store-1").Returns(Task.FromResult<PayjoinStoreSettings?>(new PayjoinStoreSettings
        {
            OhttpRelayUrls = []
        }));

        var relayClient = Substitute.For<IPayjoinReceiverRelayClient>();
        var manager = new PayjoinMailroomManager(
            NullLogger<PayjoinMailroomManager>.Instance,
            TimeSpan.FromMinutes(10),
            (_, _, _, _) => Task.FromResult(PayjoinOhttpKeysFetchResult.RetryableFailure(new HttpRequestException("unused"))));
        var sender = new PayjoinReceiverRelayRequestSender(settingsRepository, manager, relayClient);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync(
            "store-1",
            "invoice-1",
            relayUri => new TestRequestContext(relayUri),
            context => (new SystemUri(context.RelayUri), "application/http", [0x01]),
            CancellationToken.None));

        Assert.Contains("No OHTTP relay URLs are configured", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsyncThrowsItsOwnTypeWhenStoreSettingsCannotBeRead()
    {
        var settingsRepository = Substitute.For<IPayjoinStoreSettingsRepository>();
        settingsRepository.GetAsync("store-1").Returns(Task.FromResult<PayjoinStoreSettings?>(null));

        var relayClient = Substitute.For<IPayjoinReceiverRelayClient>();
        var manager = new PayjoinMailroomManager(
            NullLogger<PayjoinMailroomManager>.Instance,
            TimeSpan.FromMinutes(10),
            (_, _, _, _) => Task.FromResult(PayjoinOhttpKeysFetchResult.RetryableFailure(new HttpRequestException("unused"))));
        var sender = new PayjoinReceiverRelayRequestSender(settingsRepository, manager, relayClient);

        var exception = await Assert.ThrowsAsync<PayjoinStoreSettingsUnavailableException>(() => sender.SendAsync(
            "store-1",
            "invoice-1",
            relayUri => new TestRequestContext(relayUri),
            context => (new SystemUri(context.RelayUri), "application/http", [0x01]),
            CancellationToken.None));

        Assert.IsNotType<InvalidOperationException>(exception);
        Assert.IsNotType<ArgumentNullException>(exception);
        Assert.Contains("could not be read", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsyncUsesCurrentStoreRelaySettingsOnEachCall()
    {
        var firstRelay = new SystemUri("https://relay-1.example/");
        var secondRelay = new SystemUri("https://relay-2.example/");
        var settingsRepository = Substitute.For<IPayjoinStoreSettingsRepository>();
        settingsRepository.GetAsync("store-1").Returns(
            Task.FromResult<PayjoinStoreSettings?>(new PayjoinStoreSettings { OhttpRelayUrls = [firstRelay] }),
            Task.FromResult<PayjoinStoreSettings?>(new PayjoinStoreSettings { OhttpRelayUrls = [secondRelay] }));

        var relayClient = Substitute.For<IPayjoinReceiverRelayClient>();
        relayClient
            .SendAsync(Arg.Any<SystemUri>(), "application/http", Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new byte[] { 0xCA, 0xFE }));

        var manager = new PayjoinMailroomManager(
            NullLogger<PayjoinMailroomManager>.Instance,
            TimeSpan.FromMinutes(10),
            (_, _, _, _) => Task.FromResult(PayjoinOhttpKeysFetchResult.RetryableFailure(new HttpRequestException("unused"))));
        var sender = new PayjoinReceiverRelayRequestSender(settingsRepository, manager, relayClient);

        var firstRequest = await sender.SendAsync(
            "store-1",
            "invoice-1",
            relayUri => new TestRequestContext(relayUri),
            context => (new SystemUri(context.RelayUri), "application/http", [0x01]),
            CancellationToken.None).ConfigureAwait(true);
        using var firstRequestContext = firstRequest.RequestContext;

        var secondRequest = await sender.SendAsync(
            "store-1",
            "invoice-1",
            relayUri => new TestRequestContext(relayUri),
            context => (new SystemUri(context.RelayUri), "application/http", [0x02]),
            CancellationToken.None).ConfigureAwait(true);
        using var secondRequestContext = secondRequest.RequestContext;

        Assert.Equal(firstRelay.AbsoluteUri, firstRequest.RequestContext.RelayUri);
        Assert.Equal(secondRelay.AbsoluteUri, secondRequest.RequestContext.RelayUri);
        await settingsRepository.Received(2).GetAsync("store-1").ConfigureAwait(true);
    }

    [Fact]
    public async Task SendAsyncParksRelayAfterItTimesOutSoLaterPollsRouteAroundIt()
    {
        var relay = new SystemUri("https://relay-1.example/");
        var settingsRepository = Substitute.For<IPayjoinStoreSettingsRepository>();
        settingsRepository.GetAsync("store-1").Returns(Task.FromResult<PayjoinStoreSettings?>(new PayjoinStoreSettings
        {
            OhttpRelayUrls = [relay]
        }));

        var pollAttempts = 0;
        var relayClient = Substitute.For<IPayjoinReceiverRelayClient>();
        relayClient
            .SendAsync(Arg.Any<SystemUri>(), "application/http", Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                pollAttempts++;
                return Task.FromException<byte[]>(new PayjoinReceiverRelayTimeoutException(TimeSpan.FromSeconds(45), new OperationCanceledException()));
            });

        var manager = new PayjoinMailroomManager(
            NullLogger<PayjoinMailroomManager>.Instance,
            TimeSpan.FromMinutes(10),
            (_, _, _, _) => Task.FromResult(PayjoinOhttpKeysFetchResult.RetryableFailure(new HttpRequestException("unused"))));
        var sender = new PayjoinReceiverRelayRequestSender(settingsRepository, manager, relayClient);

        await Assert.ThrowsAsync<PayjoinReceiverRelayTimeoutException>(() => sender.SendAsync(
            "store-1",
            "invoice-1",
            relayUri => new TestRequestContext(relayUri),
            context => (new SystemUri(context.RelayUri), "application/http", [0x01]),
            CancellationToken.None)).ConfigureAwait(true);
        Assert.Equal(1, pollAttempts);

        await Assert.ThrowsAsync<PayjoinReceiverRelayTimeoutException>(() => sender.SendAsync(
            "store-1",
            "invoice-1",
            relayUri => new TestRequestContext(relayUri),
            context => (new SystemUri(context.RelayUri), "application/http", [0x02]),
            CancellationToken.None)).ConfigureAwait(true);
        Assert.True(
            pollAttempts == 1,
            $"Expected the parked relay to be skipped on the next poll, but it was polled {pollAttempts} time(s).");
    }

    private sealed class TestRequestContext(string relayUri) : IDisposable
    {
        public string RelayUri { get; } = relayUri;

        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
        }
    }
}
