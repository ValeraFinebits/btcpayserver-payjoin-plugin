using BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests;

public class ContentionPaymentBatchTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task DoesNotCancelPendingSendersBeforeExpectedPaymentsSucceed()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var successfulPayment = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken pendingSenderToken = default;
        var payments = new (string InvoiceId, Func<CancellationToken, Task<string>> PayAsync)[]
        {
            ("successful", _ => successfulPayment.Task),
            ("pending", token =>
            {
                pendingSenderToken = token;
                return WaitForCancellationAsync(token);
            })
        };

        var batch = ContentionPaymentBatch.RunAsync(payments, 1, _ => false, cancellation.Token);

        Assert.True(pendingSenderToken.CanBeCanceled);
        Assert.False(pendingSenderToken.IsCancellationRequested);
        Assert.False(batch.IsCompleted);

        successfulPayment.SetResult("transaction");
        var outcomes = await batch.WaitAsync(cancellation.Token).ConfigureAwait(true);

        Assert.True(outcomes[0].Succeeded);
        Assert.Equal("transaction", outcomes[0].TransactionId);
        Assert.False(outcomes[1].Succeeded);
        Assert.True(pendingSenderToken.IsCancellationRequested);
    }

    [Fact]
    public async Task DoesNotCancelSenderWhileItsReceiverSessionExists()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var observedSession = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionExists = true;
        CancellationToken pendingSenderToken = default;
        var payments = new (string InvoiceId, Func<CancellationToken, Task<string>> PayAsync)[]
        {
            ("successful", _ => Task.FromResult("transaction")),
            ("pending", token =>
            {
                pendingSenderToken = token;
                return WaitForCancellationAsync(token);
            })
        };

        var batch = ContentionPaymentBatch.RunAsync(payments, 1, invoiceId =>
        {
            Assert.Equal("pending", invoiceId);
            observedSession.TrySetResult();
            return Volatile.Read(ref sessionExists);
        }, cancellation.Token);

        await observedSession.Task.WaitAsync(cancellation.Token).ConfigureAwait(true);
        Assert.False(pendingSenderToken.IsCancellationRequested);
        Assert.False(batch.IsCompleted);

        Volatile.Write(ref sessionExists, false);
        var outcomes = await batch.WaitAsync(cancellation.Token).ConfigureAwait(true);

        Assert.True(outcomes[0].Succeeded);
        Assert.False(outcomes[1].Succeeded);
        Assert.True(pendingSenderToken.IsCancellationRequested);
    }

    [Fact]
    public async Task CompletedPaymentsDoNotRequireReceiverSessionRemoval()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        CancellationToken senderToken = default;
        var payments = new (string InvoiceId, Func<CancellationToken, Task<string>> PayAsync)[]
        {
            ("successful", token =>
            {
                senderToken = token;
                return Task.FromResult("transaction");
            })
        };

        var outcomes = await ContentionPaymentBatch.RunAsync(payments, 1,
            _ => throw new InvalidOperationException("Completed senders do not need a session probe."), cancellation.Token).ConfigureAwait(true);

        Assert.True(Assert.Single(outcomes).Succeeded);
        Assert.False(senderToken.IsCancellationRequested);
    }

    [Fact]
    public async Task OuterCancellationIsFatalAndPreservesSenderDiagnostics()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var payments = new (string InvoiceId, Func<CancellationToken, Task<string>> PayAsync)[]
        {
            ("successful", _ => Task.FromResult("transaction")),
            ("pending", token => WaitForCancellationAsync(token))
        };

        var batch = ContentionPaymentBatch.RunAsync(payments, 1, _ => true, cancellation.Token);
        await cancellation.CancelAsync().ConfigureAwait(true);
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batch).ConfigureAwait(true);

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Contains("successful: PaymentSucceeded=True", exception.Message, StringComparison.Ordinal);
        Assert.Contains("pending: PaymentSucceeded=False", exception.Message, StringComparison.Ordinal);
        Assert.Contains("original sender cancellation details", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AlreadyCanceledOuterTokenIsFatalEvenWhenEveryPaymentCompletes()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(true);
        var payments = new (string InvoiceId, Func<CancellationToken, Task<string>> PayAsync)[]
        {
            ("successful", _ => Task.FromResult("transaction"))
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ContentionPaymentBatch.RunAsync(payments, 1, _ => false, cancellation.Token)).ConfigureAwait(true);
    }

    [Fact]
    public async Task PaymentFailuresKeepTheirOriginalException()
    {
        var failure = new InvalidOperationException("original relay failure details");
        var payments = new (string InvoiceId, Func<CancellationToken, Task<string>> PayAsync)[]
        {
            ("failed", _ => Task.FromException<string>(failure))
        };

        var outcomes = await ContentionPaymentBatch.RunAsync(payments, 1, _ => true, CancellationToken.None).ConfigureAwait(true);

        var outcome = Assert.Single(outcomes);
        Assert.False(outcome.Succeeded);
        Assert.Same(failure, outcome.Exception);
    }

    [Fact]
    public async Task ReceiverSessionProbeFailureCancelsAndDrainsPendingSenders()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var senderStopped = false;
        var payments = new (string InvoiceId, Func<CancellationToken, Task<string>> PayAsync)[]
        {
            ("successful", _ => Task.FromResult("transaction")),
            ("pending", async token =>
            {
                try
                {
                    return await WaitForCancellationAsync(token).ConfigureAwait(false);
                }
                finally
                {
                    senderStopped = true;
                }
            })
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ContentionPaymentBatch.RunAsync(payments, 1,
                _ => throw new InvalidOperationException("session probe failed"), cancellation.Token)).ConfigureAwait(true);

        Assert.True(senderStopped);
    }

    private static async Task<string> WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return "unexpected payment completion";
        }
        catch (OperationCanceledException exception)
        {
            throw new OperationCanceledException("original sender cancellation details", exception, cancellationToken);
        }
    }
}
