using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics.CodeAnalysis;

namespace BTCPayServer.Plugins.Payjoin.Services;

public sealed class PayjoinSessionUriReader
{
    private static readonly Action<ILogger, string, Exception?> LogSessionReadFailure =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1, nameof(LogSessionReadFailure)),
            "Could not read the payjoin URI of the receiver session for invoice {InvoiceId}; rendering plain BIP21.");

    private readonly PayjoinReceiverSessionStore _receiverSessionStore;
    private readonly ILogger<PayjoinSessionUriReader>? _logger;

    public PayjoinSessionUriReader(
        PayjoinReceiverSessionStore receiverSessionStore,
        ILogger<PayjoinSessionUriReader>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(receiverSessionStore);

        _receiverSessionStore = receiverSessionStore;
        _logger = logger;
    }

    // TODO: the query is synchronous on a request thread. The fix is an asynchronous host interface.
    [SuppressMessage("Design", "CA1055:URI-like return values should not be strings", Justification = "A bitcoin: BIP21 URI is merged as text; System.Uri would re-encode its query parameters.")]
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Runs on the checkout render path: any escaping exception would turn the payer's payment page into a 500.")]
    public string? TryGetExistingPayjoinUri(string invoiceId, string destination)
    {
        try
        {
            var payjoinUri = _receiverSessionStore.GetServablePayjoinUri(invoiceId, destination);
            return string.IsNullOrWhiteSpace(payjoinUri) ? null : payjoinUri;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            if (_logger is not null)
            {
                LogSessionReadFailure(_logger, invoiceId, e);
            }

            return null;
        }
    }
}
