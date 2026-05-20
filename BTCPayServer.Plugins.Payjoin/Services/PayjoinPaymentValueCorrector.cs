using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Logging;
using NBitcoin;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

public sealed class PayjoinPaymentValueCorrector : EventHostedServiceBase
{
    private static readonly Action<ILogger, string, string, decimal, decimal, Exception?> LogPayjoinPaymentValueCorrected =
        LoggerMessage.Define<string, string, decimal, decimal>(
            LogLevel.Information,
            new EventId(1, nameof(LogPayjoinPaymentValueCorrected)),
            "Payjoin receiver payment for {InvoiceId} (tx {TxId}) corrected: raw={RawBtc} BTC, net={NetBtc} BTC");
    private static readonly Action<ILogger, string, string, Exception?> LogPayjoinPaymentValueCorrectionSkipped =
        LoggerMessage.Define<string, string>(
            LogLevel.Debug,
            new EventId(2, nameof(LogPayjoinPaymentValueCorrectionSkipped)),
            "Payjoin receiver payment correction skipped for {InvoiceId}: {Reason}");
    private static readonly Action<ILogger, string, string, Exception?> LogPayjoinPaymentValueCorrectionFailed =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(3, nameof(LogPayjoinPaymentValueCorrectionFailed)),
            "Payjoin receiver payment correction failed for {InvoiceId}: {Reason}");

    private readonly PayjoinReceiverSessionStore _sessionStore;
    private readonly PaymentService _paymentService;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly ILogger<PayjoinPaymentValueCorrector> _logger;

    public PayjoinPaymentValueCorrector(
        EventAggregator eventAggregator,
        PayjoinReceiverSessionStore sessionStore,
        PaymentService paymentService,
        PaymentMethodHandlerDictionary handlers,
        ILogger<PayjoinPaymentValueCorrector> logger)
        : base(eventAggregator, logger)
    {
        _sessionStore = sessionStore;
        _paymentService = paymentService;
        _handlers = handlers;
        _logger = logger;
    }

    protected override void SubscribeToEvents()
    {
        Subscribe<InvoiceEvent>();
    }

    protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
    {
        if (evt is not InvoiceEvent invoiceEvent ||
            invoiceEvent.Name != InvoiceEvent.ReceivedPayment ||
            invoiceEvent.Payment is null)
        {
            return;
        }

        await TryCorrectPaymentAsync(invoiceEvent).ConfigureAwait(false);
    }

    private async Task TryCorrectPaymentAsync(InvoiceEvent invoiceEvent)
    {
        var invoiceId = invoiceEvent.InvoiceId;
        var payment = invoiceEvent.Payment;
        var pmi = PaymentTypes.CHAIN.GetPaymentMethodId("BTC");
        if (payment.PaymentMethodId != pmi)
        {
            return;
        }

        if (!_sessionStore.TryGetSession(invoiceId, out var session) || session is null)
        {
            return;
        }

        if (string.IsNullOrEmpty(session.PayjoinTransactionId) || !session.ContributedInputValueSats.HasValue)
        {
            LogPayjoinPaymentValueCorrectionSkipped(_logger, invoiceId, "session missing payjoin tx id or contributed value", null);
            return;
        }

        if (!_handlers.TryGetValue(pmi, out var handler) || handler is not BitcoinLikePaymentHandler bitcoinHandler)
        {
            LogPayjoinPaymentValueCorrectionSkipped(_logger, invoiceId, "BTC payment handler unavailable", null);
            return;
        }

        var details = bitcoinHandler.ParsePaymentDetails(payment.Details);
        if (details.Outpoint.Hash.ToString() != session.PayjoinTransactionId)
        {
            return;
        }

        if (details.PayjoinInformation is not null)
        {
            return;
        }

        long rawValueSats;
        try
        {
            rawValueSats = Money.Coins(payment.Value).Satoshi;
        }
        catch (OverflowException ex)
        {
            LogPayjoinPaymentValueCorrectionFailed(_logger, invoiceId, $"overflow converting payment value to satoshis: {ex.Message}", ex);
            return;
        }

        long netValueSats;
        try
        {
            netValueSats = PayjoinReceiverAccounting.NetReceivedSats(rawValueSats, session.ContributedInputValueSats.Value);
        }
        catch (ArgumentException ex)
        {
            LogPayjoinPaymentValueCorrectionFailed(_logger, invoiceId, ex.Message, ex);
            return;
        }

        var contributedOutPoints = session.TryGetContributedInput(out var contributedOutPoint)
            ? new[] { contributedOutPoint }
            : Array.Empty<OutPoint>();
        details.PayjoinInformation = new PayjoinInformation
        {
            CoinjoinTransactionHash = details.Outpoint.Hash,
            CoinjoinValue = Money.Satoshis(netValueSats),
            ContributedOutPoints = contributedOutPoints
        };

        var netBtc = ((Money)Money.Satoshis(netValueSats)).ToDecimal(MoneyUnit.BTC);
        var rawBtc = payment.Value;
        payment.Value = netBtc;
        payment.SetDetails(bitcoinHandler, details);
        payment.UpdateAmounts();

        try
        {
            await _paymentService.UpdatePayments(new List<PaymentEntity> { payment }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPayjoinPaymentValueCorrectionFailed(_logger, invoiceId, ex.Message, ex);
            return;
        }

        LogPayjoinPaymentValueCorrected(_logger, invoiceId, session.PayjoinTransactionId, rawBtc, netBtc, null);
    }
}
