using System.Diagnostics.CodeAnalysis;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;

internal static class ContentionPaymentBatch
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public static async Task<PaymentOutcome[]> RunAsync(
        IReadOnlyList<(string InvoiceId, Func<CancellationToken, Task<string>> PayAsync)> payments,
        int expectedSuccessCount,
        Func<string, bool> receiverSessionExists,
        CancellationToken cancellationToken)
    {
        using var paymentCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var outcomes = payments.Select(payment => CaptureOutcomeAsync(() => payment.PayAsync(paymentCancellation.Token))).ToArray();
        var allOutcomes = Task.WhenAll(outcomes);

        try
        {
            while (!allOutcomes.IsCompleted && !cancellationToken.IsCancellationRequested)
            {
                var successfulPayments = outcomes.Count(outcome => outcome.IsCompletedSuccessfully && outcome.Result.Succeeded);
                if (successfulPayments == expectedSuccessCount &&
                    outcomes.Select((outcome, index) => (outcome, index))
                        .Where(payment => !payment.outcome.IsCompleted)
                        .All(payment => !receiverSessionExists(payments[payment.index].InvoiceId)))
                {
                    await paymentCancellation.CancelAsync().ConfigureAwait(false);
                    break;
                }

                await Task.WhenAny(allOutcomes, Task.Delay(PollInterval, cancellationToken)).ConfigureAwait(false);
            }

            var results = await allOutcomes.ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    $"Contention payment batch canceled before completion.{Environment.NewLine}{Describe(payments.Select(payment => payment.InvoiceId).ToArray(), results)}",
                    cancellationToken);
            }

            return results;
        }
        finally
        {
            if (!allOutcomes.IsCompleted)
            {
                await paymentCancellation.CancelAsync().ConfigureAwait(false);
                await allOutcomes.ConfigureAwait(false);
            }
        }
    }

    public static string Describe(IReadOnlyList<string> invoiceIds, IReadOnlyList<PaymentOutcome> outcomes)
    {
        return string.Join(Environment.NewLine, outcomes.Select((outcome, index) =>
            $"{invoiceIds[index]}: PaymentSucceeded={outcome.Succeeded}, PaymentError={outcome.Exception}"));
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Contention tests inspect every successful and failed payment, including the original cancellation diagnostics.")]
    private static async Task<PaymentOutcome> CaptureOutcomeAsync(Func<Task<string>> payAsync)
    {
        try
        {
            return new PaymentOutcome(true, await payAsync().ConfigureAwait(false), null);
        }
        catch (Exception exception)
        {
            return new PaymentOutcome(false, null, exception);
        }
    }
}

internal sealed record PaymentOutcome(bool Succeeded, string? TransactionId, Exception? Exception);
