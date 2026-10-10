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
        settingsRepository.GetAsync("store-1").Returns(Task.FromResult(new PayjoinStoreSettings
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
        settingsRepository.GetAsync("store-1").Returns(Task.FromResult(new PayjoinStoreSettings
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
    public async Task SendAsyncUsesCurrentStoreRelaySettingsOnEachCall()
    {
        var firstRelay = new SystemUri("https://relay-1.example/");
        var secondRelay = new SystemUri("https://relay-2.example/");
        var settingsRepository = Substitute.For<IPayjoinStoreSettingsRepository>();
        settingsRepository.GetAsync("store-1").Returns(
            Task.FromResult(new PayjoinStoreSettings { OhttpRelayUrls = [firstRelay] }),
            Task.FromResult(new PayjoinStoreSettings { OhttpRelayUrls = [secondRelay] }));

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
        settingsRepository.GetAsync("store-1").Returns(Task.FromResult(new PayjoinStoreSettings
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

    [Fact]
    public async Task SendAsyncQuarantinesTransportFailureAcrossSessions()
    {
        var firstRelay = new SystemUri("https://relay-1.example/");
        var secondRelay = new SystemUri("https://relay-2.example/");
        var settingsRepository = Substitute.For<IPayjoinStoreSettingsRepository>();
        settingsRepository.GetAsync("store-1").Returns(Task.FromResult(new PayjoinStoreSettings
        {
            OhttpRelayUrls = [firstRelay, secondRelay]
        }));

        // Relay selection shuffles, so fail whichever relay is attempted first and let the
        // retry succeed on the other one; the blocked relay is then derived from the attempt.
        SystemUri? failedRelay = null;
        var relayClient = Substitute.For<IPayjoinReceiverRelayClient>();
        relayClient
            .SendAsync(Arg.Any<SystemUri>(), "application/http", Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var relayUrl = callInfo.ArgAt<SystemUri>(0);
                if (failedRelay is null)
                {
                    failedRelay = relayUrl;
                    return Task.FromException<byte[]>(new HttpRequestException("connection refused"));
                }

                return Task.FromResult(new byte[] { 0xCA, 0xFE });
            });

        var manager = new PayjoinMailroomManager(
            NullLogger<PayjoinMailroomManager>.Instance,
            TimeSpan.FromMinutes(10),
            (_, _, _, _) => Task.FromResult(PayjoinOhttpKeysFetchResult.RetryableFailure(new HttpRequestException("unused"))));
        var sender = new PayjoinReceiverRelayRequestSender(settingsRepository, manager, relayClient);

        await sender.SendAsync(
            "store-1",
            "invoice-1",
            relayUri => new TestRequestContext(relayUri),
            context => (new SystemUri(context.RelayUri), "application/http", [0x01]),
            CancellationToken.None);

        Assert.NotNull(failedRelay);
        var healthyRelay = failedRelay!.AbsoluteUri == firstRelay.AbsoluteUri ? secondRelay : firstRelay;
        Assert.Equal(healthyRelay, manager.ChooseRelayForRequest([firstRelay, secondRelay], "invoice-1"));
        // The transport failure also applies the global quarantine, so the other session cannot
        // pick the failed relay either; it must route to the healthy one.
        Assert.Equal(healthyRelay, manager.ChooseRelayForRequest([firstRelay, secondRelay], "invoice-2"));
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.Forbidden)]
    [InlineData(System.Net.HttpStatusCode.BadGateway)]
    public async Task SendAsyncQuarantinesHttpFailureOnlyForTheRequestingSession(System.Net.HttpStatusCode status)
    {
        var relays = new[] { new SystemUri("https://relay-1.example/"), new SystemUri("https://relay-2.example/") };
        var settings = Substitute.For<IPayjoinStoreSettingsRepository>();
        settings.GetAsync("store").Returns(Task.FromResult(new PayjoinStoreSettings { OhttpRelayUrls = relays }));
        var client = Substitute.For<IPayjoinReceiverRelayClient>();
        SystemUri? rejectedRelay = null;
        client.SendAsync(Arg.Any<SystemUri>(), Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (rejectedRelay is null)
                {
                    rejectedRelay = call.ArgAt<SystemUri>(0);
                    return Task.FromException<byte[]>(new HttpRequestException("route rejected", null, status));
                }

                Assert.NotEqual(rejectedRelay, call.ArgAt<SystemUri>(0));
                return Task.FromResult(new byte[] { 1 });
            });
        var manager = new PayjoinMailroomManager(NullLogger<PayjoinMailroomManager>.Instance, TimeSpan.FromMinutes(10),
            (_, _, _, _) => throw new NotSupportedException());
        var sender = new PayjoinReceiverRelayRequestSender(settings, manager, client);
        var contexts = new List<TestRequestContext>();

        var result = await sender.SendAsync("store", "invoice-1", relay =>
        {
            var context = new TestRequestContext(relay);
            contexts.Add(context);
            return context;
        }, context => (new SystemUri(context.RelayUri), "application/http", new byte[] { 1 }), CancellationToken.None);
        using var successfulContext = result.RequestContext;

        Assert.NotNull(rejectedRelay);
        Assert.Equal(2, contexts.Count);
        Assert.True(contexts[0].Disposed);
        Assert.False(contexts[1].Disposed);
        Assert.Null(manager.ChooseRelayForRequest([rejectedRelay], "invoice-1"));
        Assert.Equal(rejectedRelay, manager.ChooseRelayForRequest([rejectedRelay], "invoice-2"));
    }

    [Theory]
    [InlineData(1, "network")]
    [InlineData(2, "network")]
    [InlineData(1, "http")]
    [InlineData(2, "http")]
    [InlineData(1, "timeout")]
    [InlineData(2, "timeout")]
    public async Task SendAsyncRetriesRecoveredRelaysAfterQuarantineExpires(int relayCount, string failure)
    {
        var relays = Enumerable.Range(1, relayCount).Select(i => new SystemUri($"https://relay-{i}.example/")).ToArray();
        var settings = Substitute.For<IPayjoinStoreSettingsRepository>();
        settings.GetAsync("store").Returns(Task.FromResult(new PayjoinStoreSettings { OhttpRelayUrls = relays }));
        var client = Substitute.For<IPayjoinReceiverRelayClient>();
        var failing = true;
        var calls = 0;
        client.SendAsync(Arg.Any<SystemUri>(), Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls++;
                if (!failing)
                {
                    return Task.FromResult(new byte[] { 1 });
                }

                Exception error = failure switch
                {
                    "timeout" => new PayjoinReceiverRelayTimeoutException(TimeSpan.FromSeconds(45), new OperationCanceledException()),
                    "http" => new HttpRequestException("temporary rejection", null, System.Net.HttpStatusCode.BadGateway),
                    _ => new HttpRequestException("temporary connection failure")
                };
                return Task.FromException<byte[]>(error);
            });
        var now = DateTimeOffset.UnixEpoch;
        var clock = Substitute.For<TimeProvider>();
        clock.GetUtcNow().Returns(_ => now);
        var quarantineDuration = TimeSpan.FromMinutes(10);
        var manager = new PayjoinMailroomManager(NullLogger<PayjoinMailroomManager>.Instance, quarantineDuration,
            (_, _, _, _) => throw new NotSupportedException(), clock);
        var sender = new PayjoinReceiverRelayRequestSender(settings, manager, client);
        Task<(byte[] ResponseBody, TestRequestContext RequestContext)> Send() => sender.SendAsync("store", "invoice",
            relay => new TestRequestContext(relay), context => (new SystemUri(context.RelayUri), "application/http", new byte[] { 1 }), CancellationToken.None);

        if (failure == "timeout")
        {
            await Assert.ThrowsAsync<PayjoinReceiverRelayTimeoutException>(() => Send());
        }
        else
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => Send());
        }
        Assert.Equal(relayCount, calls);
        failing = false;

        await Assert.ThrowsAsync<PayjoinReceiverRelayTimeoutException>(() => Send());
        Assert.Equal(relayCount, calls);
        now += quarantineDuration - TimeSpan.FromTicks(1);
        await Assert.ThrowsAsync<PayjoinReceiverRelayTimeoutException>(() => Send());
        Assert.Equal(relayCount, calls);

        now += TimeSpan.FromTicks(1);
        var result = await Send();
        using var recoveredContext = result.RequestContext;

        Assert.Equal(relayCount + 1, calls);
        Assert.Equal(new byte[] { 1 }, result.ResponseBody);
    }

    [Fact]
    public async Task SendAsyncDoesNotQuarantineOrRetryWhenCallerCancels()
    {
        var relay = new SystemUri("https://relay.example/");
        var settings = Substitute.For<IPayjoinStoreSettingsRepository>();
        settings.GetAsync("store").Returns(Task.FromResult(new PayjoinStoreSettings { OhttpRelayUrls = [relay] }));
        using var cancellation = new CancellationTokenSource();
        var client = Substitute.For<IPayjoinReceiverRelayClient>();
        client.SendAsync(Arg.Any<SystemUri>(), Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<byte[]>(cancellation.Token);
            });
        var manager = new PayjoinMailroomManager(NullLogger<PayjoinMailroomManager>.Instance, TimeSpan.FromMinutes(10),
            (_, _, _, _) => throw new NotSupportedException());
        var sender = new PayjoinReceiverRelayRequestSender(settings, manager, client);
        using var context = new TestRequestContext(relay.AbsoluteUri);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.SendAsync("store", "invoice", _ => context,
            request => (new SystemUri(request.RelayUri), "application/http", new byte[] { 1 }), cancellation.Token));

        Assert.True(context.Disposed);
        Assert.Equal(relay, manager.ChooseRelayForRequest([relay], "invoice"));
        await client.Received(1).SendAsync(Arg.Any<SystemUri>(), Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
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
