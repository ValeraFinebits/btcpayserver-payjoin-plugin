using BTCPayServer.Data;
using BTCPayServer.Logging;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics.CodeAnalysis;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal enum PayjoinInvoiceEventKind
{
    TrackingStarted,
    ProposalPrepared,
    Reconciled,
    Expired,
    Failed,
    RetryRequested
}

internal sealed record PayjoinInvoiceEvent(PayjoinInvoiceEventKind Kind, string InvoiceId, InvoiceLogs Logs)
{
    public override string ToString() => $"Payjoin invoice event {Kind} for {InvoiceId}";
}

internal interface IPayjoinInvoiceEventPublisher
{
    void Publish(PayjoinInvoiceEventKind kind, string invoiceId, string? transactionId = null);
}

internal sealed class PayjoinInvoiceEventPublisher : IPayjoinInvoiceEventPublisher
{
    internal const string MessagePrefix = "Async Payjoin: ";

    private static readonly Action<ILogger, string, Exception?> LogPublicationFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, nameof(LogPublicationFailed)),
            "Could not publish the Payjoin invoice event for {InvoiceId}.");

    private readonly EventAggregator _eventAggregator;
    private readonly ILogger<PayjoinInvoiceEventPublisher> _logger;

    public PayjoinInvoiceEventPublisher(EventAggregator eventAggregator, ILogger<PayjoinInvoiceEventPublisher> logger)
    {
        _eventAggregator = eventAggregator;
        _logger = logger;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Diagnostic event publication must not change an already committed protocol or accounting operation.")]
    public void Publish(PayjoinInvoiceEventKind kind, string invoiceId, string? transactionId = null)
    {
        try
        {
            var (message, severity) = kind switch
            {
                PayjoinInvoiceEventKind.TrackingStarted => ("Payjoin tracking started.", InvoiceEventData.EventSeverity.Info),
                PayjoinInvoiceEventKind.ProposalPrepared when !string.IsNullOrEmpty(transactionId) =>
                    ($"Payjoin proposal prepared. Awaiting transaction: {transactionId}.", InvoiceEventData.EventSeverity.Info),
                PayjoinInvoiceEventKind.Reconciled =>
                    (string.IsNullOrEmpty(transactionId) ? "Settled with Payjoin." : $"Settled with Payjoin. Transaction: {transactionId}.", InvoiceEventData.EventSeverity.Success),
                PayjoinInvoiceEventKind.Expired when transactionId is null => ("Payjoin tracking expired.", InvoiceEventData.EventSeverity.Info),
                PayjoinInvoiceEventKind.Expired =>
                    ($"Payjoin settlement remains unresolved. Expected transaction: {transactionId}. Review the Async Payjoin page.", InvoiceEventData.EventSeverity.Warning),
                PayjoinInvoiceEventKind.Failed =>
                    ("Payjoin settlement needs attention. Settlement reconciliation failed. Review the Async Payjoin page.", InvoiceEventData.EventSeverity.Warning),
                PayjoinInvoiceEventKind.RetryRequested => ("Payjoin settlement retry requested.", InvoiceEventData.EventSeverity.Info),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown or incomplete Payjoin invoice event.")
            };
            var logs = new InvoiceLogs();
            logs.Write(MessagePrefix + message, severity);
            _eventAggregator.Publish(new PayjoinInvoiceEvent(kind, invoiceId, logs));
        }
        catch (Exception ex)
        {
            LogPublicationFailed(_logger, invoiceId, ex);
        }
    }
}
