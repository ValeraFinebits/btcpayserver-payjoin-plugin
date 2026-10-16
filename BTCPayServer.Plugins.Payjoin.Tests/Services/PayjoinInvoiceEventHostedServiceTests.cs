using BTCPayServer.Logging;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Diagnostics;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinInvoiceEventHostedServiceTests
{
    private const string TransactionId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task PublishingDoesNotWaitForTheWriterAndGracefulStopDrainsTheQueueWithOriginalTimestamps()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstInvoiceAttempts = 0;
        var writer = new RecordingInvoiceEventWriter(async evt =>
        {
            if (evt.InvoiceId == "invoice-1")
            {
                var attempt = Interlocked.Increment(ref firstInvoiceAttempts);
                if (attempt == 1)
                {
                    entered.TrySetResult();
                    await resume.Task.ConfigureAwait(false);
                }
                if (attempt <= 2)
                {
                    throw new TimeoutException("Injected transient invoice-log failure during graceful stop");
                }
            }
        });
        using var events = new EventAggregator(new Logs());
        var publisher = new PayjoinInvoiceEventPublisher(events, NullLogger<PayjoinInvoiceEventPublisher>.Instance);
        var service = new PayjoinInvoiceEventHostedService(events, writer, NullLogger<PayjoinInvoiceEventHostedService>.Instance);
        await service.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var beforePublication = DateTimeOffset.UtcNow;
            publisher.Publish(PayjoinInvoiceEventKind.Reconciled, "invoice-1", TransactionId);
            var afterPublication = DateTimeOffset.UtcNow;
            await entered.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            var originalLog = Assert.Single(Assert.Single(writer.Attempts).Logs.ToList());
            Assert.InRange(originalLog.Timestamp, beforePublication, afterPublication);

            publisher.Publish(PayjoinInvoiceEventKind.RetryRequested, "invoice-2");
            var stop = service.StopAsync(CancellationToken.None);
            Assert.False(stop.IsCompleted);
            Assert.Empty(writer.Writes);
            resume.TrySetResult();
            await stop.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(new[] { "invoice-1", "invoice-1", "invoice-1", "invoice-2" }, writer.Attempts.Select(evt => evt.InvoiceId));
            Assert.Equal(new[] { "invoice-1", "invoice-2" }, writer.Writes.Select(evt => evt.InvoiceId));
            var storedLog = Assert.Single(writer.Writes.First().Logs.ToList());
            Assert.Equal(originalLog.Timestamp, storedLog.Timestamp);
            Assert.StartsWith(PayjoinInvoiceEventPublisher.MessagePrefix, storedLog.Log, StringComparison.Ordinal);
        }
        finally
        {
            resume.TrySetResult();
            await service.StopAsync(CancellationToken.None).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ReconciledEventSurvivesTransientWriteFailuresWithItsOriginalTimestamp(int failuresBeforeSuccess)
    {
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attemptTimes = new ConcurrentQueue<long>();
        var attemptCount = 0;
        var writer = new RecordingInvoiceEventWriter(_ =>
        {
            attemptTimes.Enqueue(Stopwatch.GetTimestamp());
            if (Interlocked.Increment(ref attemptCount) <= failuresBeforeSuccess)
            {
                return Task.FromException(new TimeoutException("Injected transient invoice-log failure"));
            }
            written.TrySetResult();
            return Task.CompletedTask;
        });
        var logger = new RecordingLogger<PayjoinInvoiceEventHostedService>();
        using var events = new EventAggregator(new Logs());
        PayjoinInvoiceEvent? originalEvent = null;
        using var subscription = events.Subscribe<PayjoinInvoiceEvent>(evt => originalEvent = evt);
        var publisher = new PayjoinInvoiceEventPublisher(events, NullLogger<PayjoinInvoiceEventPublisher>.Instance);
        var service = new PayjoinInvoiceEventHostedService(events, writer, logger);
        await service.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var beforePublication = DateTimeOffset.UtcNow;
            publisher.Publish(PayjoinInvoiceEventKind.Reconciled, "invoice-reconciled", TransactionId);
            var afterPublication = DateTimeOffset.UtcNow;
            Assert.NotNull(originalEvent);
            var originalLog = Assert.Single(originalEvent.Logs.ToList());
            Assert.InRange(originalLog.Timestamp, beforePublication, afterPublication);
            Assert.Equal(BTCPayServer.Data.InvoiceEventData.EventSeverity.Success, originalLog.Severity);
            Assert.Contains(TransactionId, originalLog.Log, StringComparison.Ordinal);

            await written.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            await service.StopAsync(CancellationToken.None).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(failuresBeforeSuccess + 1, writer.Attempts.Count);
            Assert.Same(originalEvent, Assert.Single(writer.Writes));
            Assert.All(writer.Attempts, attemptedEvent =>
            {
                Assert.Same(originalEvent, attemptedEvent);
                Assert.Same(originalEvent.Logs, attemptedEvent.Logs);
                var attemptedLog = Assert.Single(attemptedEvent.Logs.ToList());
                Assert.Equal(originalLog.Log, attemptedLog.Log);
                Assert.Equal(originalLog.Severity, attemptedLog.Severity);
                Assert.Equal(originalLog.Timestamp, attemptedLog.Timestamp);
            });
            Assert.Empty(logger.Entries);
            var timestamps = attemptTimes.ToArray();
            for (var i = 1; i < timestamps.Length; i++)
            {
                Assert.True(Stopwatch.GetElapsedTime(timestamps[i - 1], timestamps[i]) >= TimeSpan.FromMilliseconds(450));
            }
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task FailedWriteIsLoggedAndTheNextInvoiceStillGetsItsEvent()
    {
        var nextWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception[] failures =
        [
            new TimeoutException("Injected first invoice-log failure"),
            new InvalidOperationException("Injected second invoice-log failure"),
            new TimeoutException("Injected final invoice-log failure")
        ];
        var failedAttempts = 0;
        var writer = new RecordingInvoiceEventWriter(evt =>
        {
            if (evt.InvoiceId == "invoice-failed")
            {
                return Task.FromException(failures[Interlocked.Increment(ref failedAttempts) - 1]);
            }
            nextWrite.TrySetResult();
            return Task.CompletedTask;
        });
        var logger = new RecordingLogger<PayjoinInvoiceEventHostedService>();
        using var events = new EventAggregator(new Logs());
        var publisher = new PayjoinInvoiceEventPublisher(events, NullLogger<PayjoinInvoiceEventPublisher>.Instance);
        var service = new PayjoinInvoiceEventHostedService(events, writer, logger);
        await service.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            publisher.Publish(PayjoinInvoiceEventKind.TrackingStarted, "invoice-failed");
            publisher.Publish(PayjoinInvoiceEventKind.TrackingStarted, "invoice-next");
            await nextWrite.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            await service.StopAsync(CancellationToken.None).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(new[] { "invoice-failed", "invoice-failed", "invoice-failed", "invoice-next" }, writer.Attempts.Select(evt => evt.InvoiceId));
            Assert.Equal("invoice-next", Assert.Single(writer.Writes).InvoiceId);
            var warning = Assert.Single(logger.Entries);
            Assert.Equal(LogLevel.Warning, warning.Level);
            Assert.Contains("invoice-failed", warning.Message, StringComparison.Ordinal);
            Assert.Same(failures[^1], warning.Exception);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownDeadlineBoundsTheWaitWithoutPretendingToCancelTheOutstandingDatabaseWrite(bool writeFailsLate)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new RecordingInvoiceEventWriter(async _ =>
        {
            entered.TrySetResult();
            await resume.Task.ConfigureAwait(false);
            if (writeFailsLate)
            {
                throw new TimeoutException("Injected late invoice-log failure after the shutdown deadline");
            }
        });
        var logger = new RecordingLogger<PayjoinInvoiceEventHostedService>();
        using var events = new EventAggregator(new Logs());
        var publisher = new PayjoinInvoiceEventPublisher(events, NullLogger<PayjoinInvoiceEventPublisher>.Instance);
        var service = new PayjoinInvoiceEventHostedService(events, writer, logger);
        using var deadline = new CancellationTokenSource();
        await service.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            publisher.Publish(PayjoinInvoiceEventKind.Reconciled, "invoice-in-flight", TransactionId);
            await entered.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            publisher.Publish(PayjoinInvoiceEventKind.TrackingStarted, "invoice-queued");
            var stop = service.StopAsync(deadline.Token);
            Assert.False(stop.IsCompleted);
            deadline.Cancel();
            await stop.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            Assert.False(resume.Task.IsCompleted);
            Assert.Contains(logger.Entries, entry => entry.Message.Contains("shutdown deadline", StringComparison.Ordinal));

            resume.TrySetResult();
            await service.StopAsync(CancellationToken.None).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            if (writeFailsLate)
            {
                Assert.Empty(writer.Writes);
            }
            else
            {
                Assert.Equal("invoice-in-flight", Assert.Single(writer.Writes).InvoiceId);
            }
            Assert.Single(writer.Attempts);
            Assert.Single(logger.Entries);
        }
        finally
        {
            resume.TrySetResult();
            await service.StopAsync(CancellationToken.None).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ShutdownDeadlineDuringTheRetryPausePreventsFurtherWrites()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new RecordingInvoiceEventWriter(async _ =>
        {
            entered.TrySetResult();
            await resume.Task.ConfigureAwait(false);
            throw new TimeoutException("Injected invoice-log failure before the retry pause");
        });
        var logger = new RecordingLogger<PayjoinInvoiceEventHostedService>();
        using var events = new EventAggregator(new Logs());
        var publisher = new PayjoinInvoiceEventPublisher(events, NullLogger<PayjoinInvoiceEventPublisher>.Instance);
        var service = new PayjoinInvoiceEventHostedService(events, writer, logger);
        using var deadline = new CancellationTokenSource();
        await service.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            publisher.Publish(PayjoinInvoiceEventKind.Reconciled, "invoice-failed", TransactionId);
            await entered.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            publisher.Publish(PayjoinInvoiceEventKind.TrackingStarted, "invoice-queued");
            var stop = service.StopAsync(deadline.Token);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(100));
            resume.TrySetResult();
            await stop.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            await service.StopAsync(CancellationToken.None).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

            Assert.True(deadline.IsCancellationRequested);
            Assert.Single(writer.Attempts);
            Assert.Empty(writer.Writes);
            Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("Could not write", StringComparison.Ordinal));
        }
        finally
        {
            resume.TrySetResult();
            await service.StopAsync(CancellationToken.None).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task EventsPublishedAfterStopAreNotWritten()
    {
        var writer = new RecordingInvoiceEventWriter();
        using var events = new EventAggregator(new Logs());
        var publisher = new PayjoinInvoiceEventPublisher(events, NullLogger<PayjoinInvoiceEventPublisher>.Instance);
        var service = new PayjoinInvoiceEventHostedService(events, writer, NullLogger<PayjoinInvoiceEventHostedService>.Instance);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(CancellationToken.None).WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        publisher.Publish(PayjoinInvoiceEventKind.TrackingStarted, "invoice-after-stop");

        Assert.Empty(writer.Attempts);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception), exception));
    }
}
