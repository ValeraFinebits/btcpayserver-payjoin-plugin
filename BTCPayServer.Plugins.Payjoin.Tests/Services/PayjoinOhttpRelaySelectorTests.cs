using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Payjoin;
using SystemUri = System.Uri;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinMailroomManagerTests
{
    [Fact]
    public async Task SelectBootstrapRouteAsyncTriesAllRelaysWhenFailuresAreRetryable()
    {
        var directoryUrls = new[]
        {
            new SystemUri("https://directory-1.example/"),
            new SystemUri("https://directory-2.example/")
        };
        var relayUrls = new[]
        {
            new SystemUri("https://relay-1.example/"),
            new SystemUri("https://relay-2.example/")
        };
        var attemptedPairs = new List<(SystemUri DirectoryUrl, SystemUri RelayUrl)>();
        var selector = CreateManager((relayUrl, directoryUrl, _, _) =>
        {
            attemptedPairs.Add((new SystemUri(directoryUrl), relayUrl));
            return Task.FromResult(PayjoinOhttpKeysFetchResult.RetryableFailure(new HttpRequestException("relay down")));
        });

        var selected = await selector.SelectBootstrapRouteAsync(
            new PayjoinStoreSettings { DirectoryUrls = directoryUrls, OhttpRelayUrls = relayUrls },
            "store-1",
            "invoice-1",
            CancellationToken.None);

        Assert.Null(selected);
        Assert.Equal(directoryUrls.Length * relayUrls.Length, attemptedPairs.Count);
        Assert.All(directoryUrls, directoryUrl => Assert.Contains(attemptedPairs, pair => pair.DirectoryUrl == directoryUrl));
        Assert.All(relayUrls, relayUrl => Assert.Contains(attemptedPairs, pair => pair.RelayUrl == relayUrl));
    }

    [Fact]
    public async Task SelectBootstrapRouteAsyncFallsBackToNextDirectoryWhenFailureIsNonRetryable()
    {
        var attemptedPairs = new List<(SystemUri DirectoryUrl, SystemUri RelayUrl)>();
        var selector = CreateManager((relayUrl, directoryUrl, _, _) =>
        {
            attemptedPairs.Add((new SystemUri(directoryUrl), relayUrl));
            return Task.FromResult(PayjoinOhttpKeysFetchResult.NonRetryableFailure(new InvalidOperationException("protocol failure")));
        });

        var directoryUrls =
            new[]
            {
                new SystemUri("https://directory-1.example/"),
                new SystemUri("https://directory-2.example/")
            };
        var relayUrls =
            new[]
            {
                new SystemUri("https://relay-1.example/"),
                new SystemUri("https://relay-2.example/")
            };

        var selected = await selector.SelectBootstrapRouteAsync(
            new PayjoinStoreSettings
            {
                DirectoryUrls = directoryUrls,
                OhttpRelayUrls = relayUrls
            },
            "store-1",
            "invoice-1",
            CancellationToken.None);

        Assert.Null(selected);
        Assert.Equal(directoryUrls.Length, attemptedPairs.Count);
        Assert.All(directoryUrls, directoryUrl => Assert.Contains(attemptedPairs, pair => pair.DirectoryUrl == directoryUrl));
    }

    [Fact]
    public async Task SelectBootstrapRouteAsyncDoesNotCacheNonRetryableFailureAcrossBootstrapAttempts()
    {
        var relayUrl = new SystemUri("https://relay.example/");
        var directoryUrl = new SystemUri("https://directory.example/");
        var attempts = 0;
        var selector = CreateManager((_, _, _, _) =>
        {
            attempts++;
            return Task.FromResult(PayjoinOhttpKeysFetchResult.NonRetryableFailure(new InvalidOperationException("protocol failure")));
        });
        var settings = new PayjoinStoreSettings
        {
            DirectoryUrls = [directoryUrl],
            OhttpRelayUrls = [relayUrl]
        };

        await selector.SelectBootstrapRouteAsync(settings, "store-1", "invoice-1", CancellationToken.None);
        await selector.SelectBootstrapRouteAsync(settings, "store-1", "invoice-2", CancellationToken.None);

        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task SelectBootstrapRouteAsyncSkipsTemporarilyFailedRelay()
    {
        var firstRelay = new SystemUri("https://relay-1.example/");
        var secondRelay = new SystemUri("https://relay-2.example/");
        var attemptedRelays = new List<SystemUri>();
        var selector = CreateManager((relayUrl, _, _, _) =>
        {
            attemptedRelays.Add(relayUrl);
            return Task.FromResult(PayjoinOhttpKeysFetchResult.RetryableFailure(new HttpRequestException("relay down")));
        });
        var settings = new PayjoinStoreSettings
        {
            DirectoryUrls = [new SystemUri("https://directory.example/")],
            OhttpRelayUrls = [firstRelay, secondRelay]
        };

        await selector.SelectBootstrapRouteAsync(settings, "store-1", "invoice-1", CancellationToken.None);
        attemptedRelays.Clear();

        var selected = await selector.SelectBootstrapRouteAsync(settings, "store-1", "invoice-2", CancellationToken.None);

        Assert.Null(selected);
        Assert.Empty(attemptedRelays);
    }

    [Fact]
    public async Task SelectBootstrapRouteAsyncReturnsSelectedDirectoryAndRelay()
    {
        var expectedDirectoryUrl = new SystemUri("https://directory.example/");
        var expectedRelayUrl = new SystemUri("https://relay.example/");
        var selector = CreateManager((relayUrl, directoryUrl, _, _) =>
        {
            Assert.Equal(expectedRelayUrl, relayUrl);
            Assert.Equal(expectedDirectoryUrl.AbsoluteUri, directoryUrl);
            return Task.FromResult(CreateSuccessResult());
        });

        var selected = await selector.SelectBootstrapRouteAsync(
            new PayjoinStoreSettings
            {
                DirectoryUrls = [expectedDirectoryUrl],
                OhttpRelayUrls = [expectedRelayUrl]
            },
            "store-1",
            "invoice-1",
            CancellationToken.None);

        Assert.NotNull(selected);
        Assert.Equal(expectedDirectoryUrl, selected!.DirectoryUrl);
        Assert.Equal(expectedRelayUrl, selected.RelayUrl);
    }

    [Fact]
    public async Task SelectBootstrapRouteAsyncFallsBackToNextDirectoryAndReturnsSuccess()
    {
        var firstDirectoryUrl = new SystemUri("https://directory-1.example/");
        var secondDirectoryUrl = new SystemUri("https://directory-2.example/");
        var relayUrl = new SystemUri("https://relay.example/");
        var attemptsByDirectory = new Dictionary<SystemUri, int>();
        var selector = CreateManager((candidateRelayUrl, directoryUrl, _, _) =>
        {
            Assert.Equal(relayUrl, candidateRelayUrl);
            var attemptedDirectory = new SystemUri(directoryUrl);
            attemptsByDirectory[attemptedDirectory] = attemptsByDirectory.GetValueOrDefault(attemptedDirectory) + 1;

            return Task.FromResult(
                attemptsByDirectory.Count == 1
                    ? PayjoinOhttpKeysFetchResult.NonRetryableFailure(new InvalidOperationException("protocol failure"))
                    : CreateSuccessResult());
        });

        var selected = await selector.SelectBootstrapRouteAsync(
            new PayjoinStoreSettings
            {
                DirectoryUrls = [firstDirectoryUrl, secondDirectoryUrl],
                OhttpRelayUrls = [relayUrl]
            },
            "store-1",
            "invoice-1",
            CancellationToken.None);

        Assert.NotNull(selected);
        Assert.True(
            selected!.DirectoryUrl == firstDirectoryUrl || selected.DirectoryUrl == secondDirectoryUrl,
            $"Unexpected selected directory: {selected.DirectoryUrl}");
        Assert.Equal(relayUrl, selected.RelayUrl);
        Assert.Equal(2, attemptsByDirectory.Count);
        Assert.All(attemptsByDirectory, static pair => Assert.Equal(1, pair.Value));
        Assert.True(attemptsByDirectory.ContainsKey(firstDirectoryUrl));
        Assert.True(attemptsByDirectory.ContainsKey(secondDirectoryUrl));
        Assert.True(attemptsByDirectory.ContainsKey(selected.DirectoryUrl));
    }

    [Fact]
    public void ChooseRelayForRequestReturnsConfiguredRelay()
    {
        var expectedRelayUrl = new SystemUri("https://relay.example/");
        var selector = CreateManager((_, _, _, _) => Task.FromResult(CreateSuccessResult()));

        var selected = selector.ChooseRelayForRequest(
            new PayjoinStoreSettings
            {
                DirectoryUrls = [new SystemUri("https://directory.example/")],
                OhttpRelayUrls = [expectedRelayUrl]
            });

        Assert.Equal(expectedRelayUrl, selected);
    }

    [Fact]
    public void ChooseRelayForRequestSkipsTemporarilyUnavailableRelay()
    {
        var firstRelay = new SystemUri("https://relay-1.example/");
        var secondRelay = new SystemUri("https://relay-2.example/");
        var selector = CreateManager((_, _, _, _) => Task.FromResult(CreateSuccessResult()));
        selector.MarkRelayTemporarilyUnavailable(firstRelay);

        var selected = selector.ChooseRelayForRequest(
            new PayjoinStoreSettings
            {
                DirectoryUrls = [new SystemUri("https://directory.example/")],
                OhttpRelayUrls = [firstRelay, secondRelay]
            });

        Assert.Equal(secondRelay, selected);
    }

    [Fact]
    public void ChooseRelayForRequestReenablesRelayAfterCacheExpires()
    {
        var relayUrl = new SystemUri("https://relay.example/");
        var selector = CreateManager(
            (_, _, _, _) => Task.FromResult(CreateSuccessResult()),
            TimeSpan.Zero);
        selector.MarkRelayTemporarilyUnavailable(relayUrl);

        var selected = selector.ChooseRelayForRequest(
            new PayjoinStoreSettings
            {
                DirectoryUrls = [new SystemUri("https://directory.example/")],
                OhttpRelayUrls = [relayUrl]
            });

        Assert.Equal(relayUrl, selected);
    }

    [Fact]
    public void OrderRelayUrlsPreservesRelaySet()
    {
        var relayUrls = new[]
        {
            new SystemUri("https://relay-1.example/"),
            new SystemUri("https://relay-2.example/"),
            new SystemUri("https://relay-3.example/")
        };

        var orderedRelayUrls = PayjoinMailroomManager.OrderRelayUrls(relayUrls);

        Assert.Equal(relayUrls.OrderBy(static relayUrl => relayUrl.AbsoluteUri), orderedRelayUrls.OrderBy(static relayUrl => relayUrl.AbsoluteUri));
    }

    [Fact]
    public void OrderDirectoryUrlsPreservesDirectorySet()
    {
        var directoryUrls = new[]
        {
            new SystemUri("https://directory-1.example/"),
            new SystemUri("https://directory-2.example/"),
            new SystemUri("https://directory-3.example/")
        };

        var orderedDirectoryUrls = PayjoinMailroomManager.OrderDirectoryUrls(directoryUrls);

        Assert.Equal(directoryUrls.OrderBy(static directoryUrl => directoryUrl.AbsoluteUri), orderedDirectoryUrls.OrderBy(static directoryUrl => directoryUrl.AbsoluteUri));
    }

    private static PayjoinMailroomManager CreateManager(
        Func<SystemUri, string, string, CancellationToken, Task<PayjoinOhttpKeysFetchResult>> fetchKeysAsync,
        TimeSpan? failedRelayCacheDuration = null)
    {
        return new PayjoinMailroomManager(
            NullLogger<PayjoinMailroomManager>.Instance,
            failedRelayCacheDuration ?? TimeSpan.FromMinutes(10),
            fetchKeysAsync);
    }

    private static PayjoinOhttpKeysFetchResult CreateSuccessResult()
    {
        var ohttpKeys = OhttpKeys.Decode(Convert.FromHexString(
            "01001604ba48c49c3d4a92a3ad00ecc63a024da10ced02180c73ec12d8a7ad2cc91bb483824fe2bee8d28bfe2eb2fc6453bc4d31cd851e8a6540e86c5382af588d370957000400010003"));
        return PayjoinOhttpKeysFetchResult.Success(ohttpKeys);
    }
}
