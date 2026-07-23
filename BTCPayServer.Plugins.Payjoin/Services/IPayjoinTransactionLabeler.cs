using BTCPayServer.Services;
using Microsoft.Extensions.Logging;
using NBitcoin;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal interface IPayjoinTransactionLabeler
{
    Task LabelAsyncPayjoinAsync(WalletId walletId, uint256 transactionId, string invoiceId, CancellationToken cancellationToken);
}

internal sealed class PayjoinTransactionLabeler : IPayjoinTransactionLabeler
{
    internal const string AsyncPayjoinLabel = "Async Payjoin";

    private static readonly Action<ILogger, string, string, Exception?> LogAsyncPayjoinLabelFailed =
        LoggerMessage.Define<string, string>(LogLevel.Debug, new EventId(1, nameof(LogAsyncPayjoinLabelFailed)),
            "Failed to tag the Async Payjoin settlement transaction {TransactionId} for {InvoiceId}; the payment is unaffected.");

    private readonly WalletRepository _walletRepository;
    private readonly ILogger<PayjoinTransactionLabeler> _logger;

    public PayjoinTransactionLabeler(WalletRepository walletRepository, ILogger<PayjoinTransactionLabeler> logger)
    {
        _walletRepository = walletRepository;
        _logger = logger;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Tagging the settlement transaction is cosmetic and must never disrupt payment reconciliation.")]
    public async Task LabelAsyncPayjoinAsync(WalletId walletId, uint256 transactionId, string invoiceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await _walletRepository.AddWalletTransactionAttachment(walletId, transactionId, CreateSettlementAttachments(invoiceId)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogAsyncPayjoinLabelFailed(_logger, transactionId.ToString(), invoiceId, ex);
        }
    }

    internal static IReadOnlyList<Attachment> CreateSettlementAttachments(string invoiceId)
    {
        var attachments = new List<Attachment>(2) { CreateAsyncPayjoinAttachment() };
        if (!string.IsNullOrWhiteSpace(invoiceId))
        {
            attachments.Insert(0, Attachment.Invoice(invoiceId));
        }

        return attachments;
    }

    internal static Attachment CreateAsyncPayjoinAttachment()
    {
        return new Attachment(AsyncPayjoinLabel);
    }
}
