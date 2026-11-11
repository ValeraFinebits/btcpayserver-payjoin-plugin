using BTCPayServer.Plugins.Payjoin.Services;

namespace BTCPayServer.Plugins.Payjoin.Tests;

internal sealed class RecordingOriginalValidator : IPayjoinOriginalTransactionValidator
{
    public int Calls { get; private set; }
    public OriginalTransactionVerdict Verdict { get; set; } = OriginalTransactionVerdict.Accepted;
    public Func<Task>? BeforeResult { get; set; }
    public async Task<OriginalTransactionValidation> ValidateAsync(byte[] original, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        if (BeforeResult is not null) await BeforeResult().ConfigureAwait(false);
        return new(Verdict, "test mempool response");
    }
}
