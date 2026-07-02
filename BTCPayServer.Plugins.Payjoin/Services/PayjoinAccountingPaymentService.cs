using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Wallets;
using Microsoft.Extensions.Logging;
using NBitcoin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal interface IPayjoinAccountingPaymentService
{
    Task<PaymentEntity?> ReconcileWithFinalTransactionAsync(PayjoinAccountingBridgeState bridge, CancellationToken cancellationToken);
}

public sealed class PayjoinAccountingReconciliationDataException : Exception
{
    public PayjoinAccountingReconciliationDataException()
    {
    }

    public PayjoinAccountingReconciliationDataException(string message)
        : base(message)
    {
    }

    public PayjoinAccountingReconciliationDataException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

internal sealed class PayjoinAccountingPaymentService : IPayjoinAccountingPaymentService
{
    private static readonly Action<ILogger, string, string, Exception?> LogPayjoinAccountingFinalTransactionUnavailable =
        LoggerMessage.Define<string, string>(LogLevel.Debug, new EventId(12, nameof(LogPayjoinAccountingFinalTransactionUnavailable)),
            "Payjoin accounting final transaction {TransactionId} is not yet available for {InvoiceId}");
    private static readonly Action<ILogger, string, Exception?> LogPayjoinAccountingSettlementOutputUnavailable =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(13, nameof(LogPayjoinAccountingSettlementOutputUnavailable)),
            "Payjoin accounting could not resolve a settlement output for {InvoiceId}");
    private static readonly Action<ILogger, string, Exception?> LogPayjoinAccountingFinalPaymentUnavailable =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(14, nameof(LogPayjoinAccountingFinalPaymentUnavailable)),
            "Payjoin accounting could not record a payment for the final transaction of {InvoiceId}");
    private readonly IPayjoinInvoiceLookup _invoiceLookup;
    private readonly IPayjoinStalePaidOverCorrectionService _stalePaidOverCorrectionService;
    private readonly PaymentService _paymentService;
    private readonly EventAggregator _eventAggregator;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly BTCPayWalletProvider _walletProvider;
    private readonly ILogger<PayjoinAccountingPaymentService> _logger;

    public PayjoinAccountingPaymentService(
        IPayjoinInvoiceLookup invoiceLookup,
        IPayjoinStalePaidOverCorrectionService stalePaidOverCorrectionService,
        PaymentService paymentService,
        EventAggregator eventAggregator,
        PaymentMethodHandlerDictionary handlers,
        BTCPayNetworkProvider networkProvider,
        BTCPayWalletProvider walletProvider,
        ILogger<PayjoinAccountingPaymentService> logger)
    {
        _invoiceLookup = invoiceLookup;
        _stalePaidOverCorrectionService = stalePaidOverCorrectionService;
        _paymentService = paymentService;
        _eventAggregator = eventAggregator;
        _handlers = handlers;
        _networkProvider = networkProvider;
        _walletProvider = walletProvider;
        _logger = logger;
    }

    public async Task<PaymentEntity?> ReconcileWithFinalTransactionAsync(PayjoinAccountingBridgeState bridge, CancellationToken cancellationToken)
    {
        var contextResult = await CreateAccountingContextAsync(bridge).ConfigureAwait(false);
        if (!contextResult.Success ||
            bridge.ExpectedFinalTransactionId is null)
        {
            return null;
        }

        var accountingContext = contextResult.Context;

        var wallet = _walletProvider.GetWallet(accountingContext.Network)
            ?? throw new InvalidOperationException($"Wallet for {accountingContext.Network.CryptoCode} is not available.");
        var finalTx = await wallet.GetTransactionAsync(uint256.Parse(bridge.ExpectedFinalTransactionId), true, cancellationToken).ConfigureAwait(false);
        if (finalTx?.Transaction is null)
        {
            LogPayjoinAccountingFinalTransactionUnavailable(_logger, bridge.ExpectedFinalTransactionId, bridge.InvoiceId, null);
            return null;
        }

        var outputIndex = ResolveFinalOutputIndex(finalTx.Transaction, bridge);
        if (outputIndex is null)
        {
            LogPayjoinAccountingSettlementOutputUnavailable(_logger, bridge.InvoiceId, null);
            return null;
        }

        var accountedValueSats = ResolveAccountedValueSats(bridge);
        if (!accountedValueSats.HasValue)
        {
            return null;
        }

        var finalTransactionId = finalTx.Transaction.GetHash();
        var finalTransactionRbf = finalTx.Transaction.RBF;
        var finalOutPoint = new OutPoint(finalTransactionId, outputIndex.Value);

        var finalPayment = FindPaymentByOutPoint(accountingContext, finalOutPoint);
        var trackedPayment = FindTrackedPayment(accountingContext, bridge);
        if (ShouldWaitForFinalTransactionConfirmation(finalPayment is not null, trackedPayment is not null, finalTx.Confirmations))
        {
            // The tracked fallback payment reflects what is currently observable on-chain, and the
            // final transaction conflicts with it. The fallback payment keeps crediting the invoice
            // until the final transaction confirms; it then receives its own payment record below.
            return null;
        }

        if (finalPayment is null)
        {
            var paymentData = CreateObservedPaymentData(accountingContext, accountedValueSats.Value, finalTransactionId, outputIndex.Value, finalTx.Confirmations, finalTransactionRbf);
            finalPayment = await _paymentService.AddPayment(paymentData, [bridge.ExpectedFinalTransactionId]).ConfigureAwait(false);
            if (finalPayment is null)
            {
                var refreshedContextResult = await CreateAccountingContextAsync(bridge).ConfigureAwait(false);
                if (refreshedContextResult.Success)
                {
                    accountingContext = refreshedContextResult.Context;
                    finalPayment = FindPaymentByOutPoint(accountingContext, finalOutPoint);
                    trackedPayment = FindTrackedPayment(accountingContext, bridge);
                }
            }

            if (finalPayment is null)
            {
                LogPayjoinAccountingFinalPaymentUnavailable(_logger, bridge.InvoiceId, null);
                return null;
            }
        }

        ApplyFinalPaymentState(accountingContext, finalPayment, finalTx.Confirmations, finalTransactionId, outputIndex.Value, accountedValueSats.Value, finalTransactionRbf);

        var updatedPayments = new List<PaymentEntity> { finalPayment };
        if (trackedPayment is not null &&
            trackedPayment.Id != finalPayment.Id &&
            trackedPayment.Accounted &&
            finalTx.Confirmations >= 1)
        {
            // Once the final transaction has confirmed, the fallback transaction it replaces can no
            // longer confirm, so its payment stops counting toward the invoice. This mirrors how the
            // platform retires payments whose transaction was replaced, and keeps the fallback payment
            // record intact under its own id in case the fallback ever needs to be re-examined.
            trackedPayment.Status = PaymentStatus.Unaccounted;
            updatedPayments.Add(trackedPayment);
        }

        await _paymentService.UpdatePayments(updatedPayments).ConfigureAwait(false);
        await _stalePaidOverCorrectionService.ClearStalePaidOverAsync(bridge.InvoiceId).ConfigureAwait(false);
        _eventAggregator.Publish(new InvoiceNeedUpdateEvent(accountingContext.Invoice.Id));
        return finalPayment;
    }

    internal static bool ShouldWaitForFinalTransactionConfirmation(bool finalPaymentExists, bool trackedPaymentExists, long confirmations)
    {
        return !finalPaymentExists && trackedPaymentExists && confirmations < 1;
    }

    private static PaymentEntity? FindPaymentByOutPoint(AccountingContext accountingContext, OutPoint outPoint)
    {
        return accountingContext.Invoice.GetPayments(false)
            .FirstOrDefault(p => p.Id == outPoint.ToString() && p.PaymentMethodId == accountingContext.PaymentMethodId);
    }

    private static PaymentData CreateObservedPaymentData(AccountingContext accountingContext, long accountedValueSats, uint256 transactionId, uint outputIndex, long confirmations, bool rbf)
    {
        var details = CreatePaymentDetails(transactionId, outputIndex, rbf, confirmations);
        return new PaymentData
        {
            Id = new OutPoint(transactionId, outputIndex).ToString(),
            Created = DateTimeOffset.UtcNow,
            Status = confirmations >= NBXplorerListener.ConfirmationRequired(accountingContext.Invoice, details)
                ? PaymentStatus.Settled
                : PaymentStatus.Processing,
            Amount = Money.Satoshis(accountedValueSats).ToDecimal(MoneyUnit.BTC),
            Currency = accountingContext.Network.CryptoCode
        }.Set(accountingContext.Invoice, accountingContext.Handler, details);
    }

    internal static uint? ResolveFinalOutputIndex(Transaction finalTransaction, PayjoinAccountingBridgeState bridge)
    {
        if (!bridge.ExpectedFinalOutputIndex.HasValue)
        {
            return FindSettlementOutputIndex(finalTransaction, bridge);
        }

        var expectedOutputIndex = bridge.ExpectedFinalOutputIndex.Value;
        if (expectedOutputIndex >= 0 && expectedOutputIndex < finalTransaction.Outputs.Count)
        {
            if (string.IsNullOrWhiteSpace(bridge.SettlementScript))
            {
                return checked((uint)expectedOutputIndex);
            }

            var settlementScript = GetSettlementScript(bridge);
            if (finalTransaction.Outputs[(int)expectedOutputIndex].ScriptPubKey == settlementScript)
            {
                return checked((uint)expectedOutputIndex);
            }
        }

        return FindSettlementOutputIndex(finalTransaction, bridge);
    }

    private static long? ResolveAccountedValueSats(PayjoinAccountingBridgeState bridge)
    {
        return bridge.EffectiveInvoiceValueSats ?? bridge.FallbackValueSats;
    }

    private static PaymentEntity? FindTrackedPayment(AccountingContext accountingContext, PayjoinAccountingBridgeState bridge)
    {
        var trackedPaymentId = ResolveTrackedPaymentId(bridge);
        if (trackedPaymentId is null)
        {
            return null;
        }

        return accountingContext.Invoice.GetPayments(false)
            .FirstOrDefault(p => p.PaymentMethodId == accountingContext.PaymentMethodId &&
                                 p.Id == trackedPaymentId);
    }

    private static void ApplyFinalPaymentState(AccountingContext accountingContext, PaymentEntity payment, long confirmations, uint256 transactionId, uint outputIndex, long accountedValueSats, bool rbf)
    {
        payment.Value = Money.Satoshis(accountedValueSats).ToDecimal(MoneyUnit.BTC);
        payment.Status = confirmations >= NBXplorerListener.ConfirmationRequired(accountingContext.Invoice, CreatePaymentDetails(transactionId, outputIndex, rbf))
            ? PaymentStatus.Settled
            : PaymentStatus.Processing;
        payment.SetDetails(accountingContext.Handler, CreatePaymentDetails(transactionId, outputIndex, rbf, confirmations));
    }

    private static uint? FindSettlementOutputIndex(Transaction finalTransaction, PayjoinAccountingBridgeState bridge)
    {
        if (string.IsNullOrWhiteSpace(bridge.SettlementScript))
        {
            return null;
        }

        var settlementScript = GetSettlementScript(bridge);
        var matches = finalTransaction.Outputs
            .Select((output, index) => new { output.ScriptPubKey, Index = index })
            .Where(x => x.ScriptPubKey == settlementScript)
            .Take(2)
            .ToArray();

        return matches.Length switch
        {
            0 => null,
            1 => checked((uint)matches[0].Index),
            _ => throw new PayjoinAccountingReconciliationDataException($"Ambiguous settlement script persisted for invoice '{bridge.InvoiceId}': multiple final transaction outputs matched.")
        };
    }

    private static Script GetSettlementScript(PayjoinAccountingBridgeState bridge)
    {
        try
        {
            return Script.FromBytesUnsafe(Convert.FromHexString(bridge.SettlementScript!));
        }
        catch (FormatException ex)
        {
            throw new PayjoinAccountingReconciliationDataException($"Invalid settlement script persisted for invoice '{bridge.InvoiceId}'.", ex);
        }
    }

    private async Task<(bool Success, AccountingContext Context)> CreateAccountingContextAsync(PayjoinAccountingBridgeState bridge)
    {
        var paymentMethodId = PaymentMethodId.Parse(bridge.PaymentMethodId);
        if (!_handlers.TryGetValue(paymentMethodId, out var paymentHandler) || paymentHandler is not BitcoinLikePaymentHandler handler)
        {
            return default;
        }

        var network = _networkProvider.GetNetwork<BTCPayNetwork>(bridge.CryptoCode);
        if (network is null)
        {
            return default;
        }

        var invoice = await _invoiceLookup.GetInvoiceAsync(bridge.InvoiceId).ConfigureAwait(false);
        if (invoice is null)
        {
            return default;
        }

        return (true, new AccountingContext(invoice, handler, paymentMethodId, network));
    }

    internal static string? ResolveTrackedPaymentId(PayjoinAccountingBridgeState bridge)
    {
        return bridge.FallbackTransactionId is not null && bridge.FallbackOutputIndex.HasValue
            ? new OutPoint(uint256.Parse(bridge.FallbackTransactionId), checked((uint)bridge.FallbackOutputIndex.Value)).ToString()
            : null;
    }

    private static BitcoinLikePaymentData CreatePaymentDetails(uint256 txId, uint outputIndex, bool rbf, long confirmations = -1)
    {
        return new BitcoinLikePaymentData
        {
            Outpoint = new OutPoint(txId, outputIndex),
            RBF = rbf,
            ConfirmationCount = confirmations
        };
    }

    private readonly record struct AccountingContext(
        InvoiceEntity Invoice,
        BitcoinLikePaymentHandler Handler,
        PaymentMethodId PaymentMethodId,
        BTCPayNetwork Network);
}
