using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Payjoin;
using Payjoin.Http;
using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SystemUri = System.Uri;

namespace BTCPayServer.Plugins.Payjoin.Services;

public sealed class PayjoinOhttpKeysProvider
{
    // TODO: Consider making this configurable if 12 hours is not a good duration for caching OHTTP keys.
    private static readonly TimeSpan OhttpKeysCacheDuration = TimeSpan.FromHours(12);

    // TODO: Consider making this configurable if 10 seconds is not a good timeout for fetching OHTTP keys.
    private static readonly TimeSpan OhttpKeysFetchTimeout = TimeSpan.FromSeconds(10);

    private static readonly Action<ILogger, string, string, Exception?> LogFetchFailure =
        LoggerMessage.Define<string, string>(
            LogLevel.Error,
            new EventId(1, nameof(FetchKeysAsync)),
            "Failed to fetch OHTTP keys from {OhttpRelayUrl} for store {StoreId}");

    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<PayjoinOhttpKeysProvider> _logger;
    private readonly Func<SystemUri, string, CancellationToken, Task<OhttpKeys>> _fetchOhttpKeysAsync;
    private readonly TimeSpan _ohttpKeysFetchTimeout;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fetchLocks = new();

    public PayjoinOhttpKeysProvider(IMemoryCache memoryCache, ILogger<PayjoinOhttpKeysProvider> logger)
        : this(memoryCache, logger, FetchOhttpKeysAsync, OhttpKeysFetchTimeout)
    {
    }

    internal PayjoinOhttpKeysProvider(
        IMemoryCache memoryCache,
        ILogger<PayjoinOhttpKeysProvider> logger,
        Func<SystemUri, string, CancellationToken, Task<OhttpKeys>> fetchOhttpKeysAsync,
        TimeSpan ohttpKeysFetchTimeout)
    {
        ArgumentNullException.ThrowIfNull(memoryCache);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(fetchOhttpKeysAsync);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ohttpKeysFetchTimeout, TimeSpan.Zero);
        _memoryCache = memoryCache;
        _logger = logger;
        _fetchOhttpKeysAsync = fetchOhttpKeysAsync;
        _ohttpKeysFetchTimeout = ohttpKeysFetchTimeout;
    }

    internal async Task<OhttpKeys?> GetKeysAsync(
        SystemUri ohttpRelayUrl,
        string directoryUrl,
        string storeId,
        CancellationToken cancellationToken)
    {
        var result = await FetchKeysAsync(ohttpRelayUrl, directoryUrl, storeId, cancellationToken).ConfigureAwait(false);
        return result.OhttpKeys;
    }

    internal async Task<PayjoinOhttpKeysFetchResult> FetchKeysAsync(
        SystemUri ohttpRelayUrl,
        string directoryUrl,
        string storeId,
        CancellationToken cancellationToken)
    {
        var cacheKey = CreateCacheKey(storeId, ohttpRelayUrl, directoryUrl);

        if (_memoryCache.TryGetValue(cacheKey, out OhttpKeys? ohttpKeys) && ohttpKeys is not null)
        {
            return PayjoinOhttpKeysFetchResult.Success(ohttpKeys!);
        }

        using var timeoutCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellationTokenSource.CancelAfter(_ohttpKeysFetchTimeout);
        var semaphore = _fetchLocks.GetOrAdd(cacheKey, static _ => new SemaphoreSlim(1, 1));
        var semaphoreAcquired = false;
        try
        {
            await semaphore.WaitAsync(timeoutCancellationTokenSource.Token).ConfigureAwait(false);
            semaphoreAcquired = true;

            if (_memoryCache.TryGetValue(cacheKey, out ohttpKeys) && ohttpKeys is not null)
            {
                return PayjoinOhttpKeysFetchResult.Success(ohttpKeys!);
            }

            ohttpKeys = await _fetchOhttpKeysAsync(ohttpRelayUrl, directoryUrl, timeoutCancellationTokenSource.Token).ConfigureAwait(false);

            var cacheOptions = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(OhttpKeysCacheDuration)
                .RegisterPostEvictionCallback(static (_, value, _, _) => (value as IDisposable)?.Dispose());
            _memoryCache.Set(cacheKey, ohttpKeys, cacheOptions);
            return PayjoinOhttpKeysFetchResult.Success(ohttpKeys);
        }
        catch (HttpRequestException e)
        {
            LogFetchFailure(_logger, ohttpRelayUrl.AbsoluteUri, storeId, e);
            return PayjoinOhttpKeysFetchResult.RetryableFailure(e);
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            LogFetchFailure(_logger, ohttpRelayUrl.AbsoluteUri, storeId, e);
            return PayjoinOhttpKeysFetchResult.RetryableFailure(e);
        }
        catch (UniffiException e)
        {
            LogFetchFailure(_logger, ohttpRelayUrl.AbsoluteUri, storeId, e);
            return PayjoinOhttpKeysFetchResult.NonRetryableFailure(e);
        }
        finally
        {
            if (semaphoreAcquired)
            {
                semaphore.Release();
            }
        }
    }

    internal static string CreateCacheKey(string storeId, SystemUri ohttpRelayUrl, string directoryUrl)
    {
        return $"PayjoinOhttpKeys_{storeId}_{ohttpRelayUrl.AbsoluteUri}_{directoryUrl}";
    }

    private static async Task<OhttpKeys> FetchOhttpKeysAsync(SystemUri ohttpRelayUrl, string directoryUrl, CancellationToken cancellationToken)
    {
        using var ohttpKeysClient = new OhttpKeysClient(ohttpRelayUrl);
        return await ohttpKeysClient.GetOhttpKeysAsync(new SystemUri(directoryUrl), cancellationToken).ConfigureAwait(false);
    }
}

internal enum PayjoinOhttpKeysFetchStatus
{
    Success,
    RetryableFailure,
    NonRetryableFailure
}

internal sealed class PayjoinOhttpKeysFetchResult
{
    private PayjoinOhttpKeysFetchResult(PayjoinOhttpKeysFetchStatus status, OhttpKeys? ohttpKeys, Exception? exception)
    {
        Status = status;
        OhttpKeys = ohttpKeys;
        Exception = exception;
    }

    internal PayjoinOhttpKeysFetchStatus Status { get; }

    internal OhttpKeys? OhttpKeys { get; }

    internal Exception? Exception { get; }

    internal static PayjoinOhttpKeysFetchResult Success(OhttpKeys ohttpKeys)
    {
        ArgumentNullException.ThrowIfNull(ohttpKeys);
        return new PayjoinOhttpKeysFetchResult(PayjoinOhttpKeysFetchStatus.Success, ohttpKeys, null);
    }

    internal static PayjoinOhttpKeysFetchResult RetryableFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new PayjoinOhttpKeysFetchResult(PayjoinOhttpKeysFetchStatus.RetryableFailure, null, exception);
    }

    internal static PayjoinOhttpKeysFetchResult NonRetryableFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new PayjoinOhttpKeysFetchResult(PayjoinOhttpKeysFetchStatus.NonRetryableFailure, null, exception);
    }
}
