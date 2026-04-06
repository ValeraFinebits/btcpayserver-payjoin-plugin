using System.Diagnostics;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;

internal static class AsyncPolling
{
    public static async Task WaitUntilAsync(
        TimeSpan timeout,
        TimeSpan pollInterval,
        Func<CancellationToken, Task<bool>> predicate,
        Func<Exception, bool>? shouldRetry,
        Func<Exception?, string> timeoutMessageFactory,
        CancellationToken cancellationToken)
    {
        ValidateArguments(timeout, pollInterval, predicate, timeoutMessageFactory);

        var stopwatch = Stopwatch.StartNew();
        Exception? lastException = null;
        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (await predicate(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                lastException = null;
            }
            catch (Exception ex) when (shouldRetry?.Invoke(ex) == true)
            {
                lastException = ex;
            }

            var remainingDelay = timeout - stopwatch.Elapsed;
            if (remainingDelay <= TimeSpan.Zero)
            {
                break;
            }

            var delay = remainingDelay < pollInterval ? remainingDelay : pollInterval;
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException(timeoutMessageFactory(lastException), lastException);
    }

    public static async Task WaitUntilAsync(
        TimeSpan timeout,
        TimeSpan pollInterval,
        Func<CancellationToken, Task<bool>> predicate,
        Func<Exception, bool>? shouldRetry,
        Func<Exception?, string> timeoutMessageFactory,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ValidateArguments(timeout, pollInterval, predicate, timeoutMessageFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var startedAt = timeProvider.GetTimestamp();
        Exception? lastException = null;
        while (timeProvider.GetElapsedTime(startedAt) < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (await predicate(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                lastException = null;
            }
            catch (Exception ex) when (shouldRetry?.Invoke(ex) == true)
            {
                lastException = ex;
            }

            var remainingDelay = timeout - timeProvider.GetElapsedTime(startedAt);
            if (remainingDelay <= TimeSpan.Zero)
            {
                break;
            }

            var delay = remainingDelay < pollInterval ? remainingDelay : pollInterval;
            await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException(timeoutMessageFactory(lastException), lastException);
    }

    private static void ValidateArguments(
        TimeSpan timeout,
        TimeSpan pollInterval,
        Func<CancellationToken, Task<bool>> predicate,
        Func<Exception?, string> timeoutMessageFactory)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be greater than zero.");
        }

        if (pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval), pollInterval, "Poll interval must be greater than zero.");
        }

        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(timeoutMessageFactory);
    }
}
