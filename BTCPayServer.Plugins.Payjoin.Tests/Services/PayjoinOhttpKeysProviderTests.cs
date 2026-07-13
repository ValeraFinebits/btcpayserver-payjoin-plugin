using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Payjoin;
using System.Diagnostics;
using Xunit;
using SystemUri = System.Uri;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinOhttpKeysProviderTests
{
    [Fact]
    public async Task FetchKeysAsyncReturnsRetryableFailureWhenRelayTimesOut()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var provider = new PayjoinOhttpKeysProvider(
            memoryCache,
            NullLogger<PayjoinOhttpKeysProvider>.Instance,
            NeverCompletesAsync,
            TimeSpan.FromMilliseconds(50));

        var stopwatch = Stopwatch.StartNew();
        var result = await provider.FetchKeysAsync(
            new SystemUri("https://relay.example/"),
            "https://directory.example/",
            "store-1",
            TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.Equal(PayjoinOhttpKeysFetchStatus.RetryableFailure, result.Status);
        Assert.IsAssignableFrom<OperationCanceledException>(result.Exception);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task FetchKeysAsyncUsesOneTimeoutBudgetWhileWaitingForAnExistingFetch()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var firstFetchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new PayjoinOhttpKeysProvider(
            memoryCache,
            NullLogger<PayjoinOhttpKeysProvider>.Instance,
            async (_, _, cancellationToken) =>
            {
                firstFetchStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("The delay should only complete by cancellation.");
            },
            TimeSpan.FromSeconds(1));
        var relayUrl = new SystemUri("https://relay.example/");
        var stopwatch = Stopwatch.StartNew();

        var firstFetch = provider.FetchKeysAsync(relayUrl, "https://directory.example/", "store-1", TestContext.Current.CancellationToken);
        await firstFetchStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var secondFetch = provider.FetchKeysAsync(relayUrl, "https://directory.example/", "store-1", TestContext.Current.CancellationToken);

        var results = await Task.WhenAll(firstFetch, secondFetch)
            .WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.All(results, result => Assert.Equal(PayjoinOhttpKeysFetchStatus.RetryableFailure, result.Status));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1.5));
    }

    [Fact]
    public async Task FetchKeysAsyncPropagatesCallerCancellationWhileWaitingForExistingFetch()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var firstFetchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new PayjoinOhttpKeysProvider(
            memoryCache,
            NullLogger<PayjoinOhttpKeysProvider>.Instance,
            async (_, _, cancellationToken) =>
            {
                firstFetchStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("The delay should only complete by cancellation.");
            },
            TimeSpan.FromSeconds(1));
        var relayUrl = new SystemUri("https://relay.example/");

        using var firstCancellationTokenSource = new CancellationTokenSource();
        var firstFetch = provider.FetchKeysAsync(relayUrl, "https://directory.example/", "store-1", firstCancellationTokenSource.Token);
        await firstFetchStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        using var cancellationTokenSource = new CancellationTokenSource();
        var secondFetch = provider.FetchKeysAsync(relayUrl, "https://directory.example/", "store-1", cancellationTokenSource.Token);
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondFetch);
        firstCancellationTokenSource.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstFetch);
    }

    [Fact]
    public async Task FetchKeysAsyncPropagatesCallerCancellation()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var provider = new PayjoinOhttpKeysProvider(
            memoryCache,
            NullLogger<PayjoinOhttpKeysProvider>.Instance,
            NeverCompletesAsync,
            TimeSpan.FromSeconds(1));
        using var cancellationTokenSource = new CancellationTokenSource();

        var fetchTask = provider.FetchKeysAsync(
            new SystemUri("https://relay.example/"),
            "https://directory.example/",
            "store-1",
            cancellationTokenSource.Token);
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetchTask);
    }

    private static async Task<OhttpKeys> NeverCompletesAsync(SystemUri relayUrl, string directoryUrl, CancellationToken cancellationToken)
    {
        _ = relayUrl;
        _ = directoryUrl;
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException("The delay should only complete by cancellation.");
    }
}
