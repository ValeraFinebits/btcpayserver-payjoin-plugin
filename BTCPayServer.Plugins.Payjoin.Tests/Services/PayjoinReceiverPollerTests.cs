using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinReceiverPollerTests
{
    [Fact]
    public async Task ExecuteAsyncLogsAndContinuesAfterGetSessionsFailure()
    {
        // Arrange
        var logger = new TestLogger<PayjoinReceiverPoller>();
        var sessionProcessor = new ThrowingOnceSessionProcessor();
        using var poller = new PayjoinReceiverPoller(
            sessionProcessor,
            logger);
        using var cancellationTokenSource = new CancellationTokenSource();

        // Act
        await poller.StartAsync(cancellationTokenSource.Token).ConfigureAwait(true);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(12);
        while (!sessionProcessor.HasSuccessfulExecution && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), global::Xunit.TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
        cancellationTokenSource.Cancel();
        await poller.StopAsync(CancellationToken.None).ConfigureAwait(true);

        // Assert
        var logEntry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, logEntry.LogLevel);
        Assert.Equal(new EventId(1, "LogPayjoinReceiverPollingFailed"), logEntry.EventId);
        var exception = Assert.IsType<InvalidOperationException>(logEntry.Exception);
        Assert.Equal("Simulated session load failure.", exception.Message);
        Assert.True(sessionProcessor.HasSuccessfulExecution);
    }

    private sealed class ThrowingOnceSessionProcessor : IPayjoinReceiverSessionProcessor
    {
        private int _hasThrown;

        public bool HasSuccessfulExecution { get; private set; }

        public async Task ProcessTickAsync(CancellationToken stoppingToken)
        {
            await Task.Yield();
            if (Interlocked.Exchange(ref _hasThrown, 1) == 0)
            {
                throw new InvalidOperationException("Simulated session load failure.");
            }

            HasSuccessfulExecution = true;
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, eventId, exception));
        }

        public sealed record LogEntry(LogLevel LogLevel, EventId EventId, Exception? Exception);

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}
