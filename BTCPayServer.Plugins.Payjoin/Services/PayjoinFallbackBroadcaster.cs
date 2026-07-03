using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Wallets;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Payjoin;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

/// <summary>
/// The receiver's BIP 78 safety net: when a session ends without the payjoin completing, the
/// sender's signed original transaction is broadcast so the merchant still gets paid. Best-effort -
/// a failure here never blocks session removal.
/// </summary>
internal interface IPayjoinFallbackBroadcaster
{
    Task TryBroadcastFallbackSafetyNetAsync(PayjoinReceiverSessionState session, CancellationToken cancellationToken);
}

internal sealed class PayjoinFallbackBroadcaster : IPayjoinFallbackBroadcaster
{
    private static readonly Action<ILogger, string, string, Exception?> LogFallbackBroadcastAttempt =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(1, nameof(LogFallbackBroadcastAttempt)),
            "Payjoin receiver session for {InvoiceId} ended without a completed payjoin; broadcasting the sender's original transaction {TransactionId} as the fallback.");
    private static readonly Action<ILogger, string, string, Exception?> LogFallbackBroadcastRejected =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(2, nameof(LogFallbackBroadcastRejected)),
            "Payjoin fallback broadcast for {InvoiceId} was not accepted: {Reason}. This is expected when a conflicting transaction already exists.");
    private static readonly Action<ILogger, string, Exception?> LogFallbackBroadcastFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(3, nameof(LogFallbackBroadcastFailed)),
            "Payjoin fallback broadcast for {InvoiceId} failed.");

    private readonly IPayjoinAccountingBridgeService _accountingBridgeService;
    private readonly PayjoinReceiverSessionStore _sessionStore;
    private readonly IPayjoinInvoiceLookup _invoiceLookup;
    private readonly BTCPayWalletProvider _walletProvider;
    private readonly ExplorerClientProvider _explorerClientProvider;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly ILogger<PayjoinFallbackBroadcaster> _logger;

    public PayjoinFallbackBroadcaster(
        IPayjoinAccountingBridgeService accountingBridgeService,
        PayjoinReceiverSessionStore sessionStore,
        IPayjoinInvoiceLookup invoiceLookup,
        BTCPayWalletProvider walletProvider,
        ExplorerClientProvider explorerClientProvider,
        BTCPayNetworkProvider networkProvider,
        ILogger<PayjoinFallbackBroadcaster> logger)
    {
        _accountingBridgeService = accountingBridgeService;
        _sessionStore = sessionStore;
        _invoiceLookup = invoiceLookup;
        _walletProvider = walletProvider;
        _explorerClientProvider = explorerClientProvider;
        _networkProvider = networkProvider;
        _logger = logger;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The fallback broadcast is a best-effort safety net and must never block session removal.")]
    public async Task TryBroadcastFallbackSafetyNetAsync(PayjoinReceiverSessionState session, CancellationToken cancellationToken)
    {
        try
        {
            var bridge = await _accountingBridgeService.TryGetByInvoiceIdAsync(session.InvoiceId, cancellationToken).ConfigureAwait(false);
            if (bridge is null)
            {
                return;
            }

            var invoice = await _invoiceLookup.GetInvoiceAsync(session.InvoiceId).ConfigureAwait(false);
            if (!ShouldAttemptBroadcast(invoice?.GetInvoiceState().Status, bridge.Status, bridge.FallbackTransactionId is not null))
            {
                return;
            }

            var network = _networkProvider.GetNetwork<BTCPayNetwork>(bridge.CryptoCode);
            if (network is null)
            {
                return;
            }

            // Never compete with the payjoin itself: if the expected final transaction is already
            // known to the wallet (mempool or chain), broadcasting the conflicting original could
            // interfere with it, and reconciliation is still going to credit the payjoin.
            if (bridge.ExpectedFinalTransactionId is not null)
            {
                var wallet = _walletProvider.GetWallet(network);
                var finalTx = wallet is null
                    ? null
                    : await wallet.GetTransactionAsync(uint256.Parse(bridge.ExpectedFinalTransactionId), true, cancellationToken).ConfigureAwait(false);
                if (finalTx?.Transaction is not null)
                {
                    return;
                }
            }

            var fallbackTx = TryLoadFallbackTransaction(session, network.NBitcoinNetwork);
            if (fallbackTx is null)
            {
                return;
            }

            LogFallbackBroadcastAttempt(_logger, session.InvoiceId, fallbackTx.GetHash().ToString(), null);
            var explorerClient = _explorerClientProvider.GetExplorerClient(network);
            var result = await explorerClient.BroadcastAsync(fallbackTx, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                LogFallbackBroadcastRejected(_logger, session.InvoiceId, $"{result.RPCCode} {result.RPCMessage}", null);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFallbackBroadcastFailed(_logger, session.InvoiceId, ex);
        }
    }

    internal static bool ShouldAttemptBroadcast(InvoiceStatus? invoiceStatus, PayjoinAccountingBridgeStatus bridgeStatus, bool hasFallback)
    {
        if (!hasFallback)
        {
            return false;
        }

        // Reconciled means the payjoin already paid the invoice. Anything but an unpaid invoice means
        // funds moved some other way, and broadcasting the original could charge the sender again.
        if (bridgeStatus == PayjoinAccountingBridgeStatus.Reconciled)
        {
            return false;
        }

        return invoiceStatus is InvoiceStatus.New or InvoiceStatus.Expired;
    }

    private Transaction? TryLoadFallbackTransaction(PayjoinReceiverSessionState session, Network network)
    {
        var persister = _sessionStore.CreatePersister(session);
        using var replay = PayjoinMethods.ReplayReceiverEventLog(persister);
        using var history = replay.SessionHistory();
        var fallbackBytes = history.FallbackTx();
        if (fallbackBytes is null || fallbackBytes.Length == 0)
        {
            return null;
        }

        return Transaction.Load(fallbackBytes, network);
    }
}
