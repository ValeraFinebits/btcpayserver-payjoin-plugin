using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Wallets;
using NBitcoin;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using System;
using System.Data.Common;
using System.IO;
using System.Net.Http;
using BTCPayServer.Plugins.Payjoin.Models;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

public sealed class PayjoinAvailabilityService
{
    private readonly StoreRepository _storeRepository;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly BTCPayWalletProvider _walletProvider;
    private readonly ExplorerClientProvider _explorerClientProvider;

    public PayjoinAvailabilityService(
        StoreRepository storeRepository,
        PaymentMethodHandlerDictionary handlers,
        BTCPayWalletProvider walletProvider,
        ExplorerClientProvider explorerClientProvider)
    {
        _storeRepository = storeRepository;
        _handlers = handlers;
        _walletProvider = walletProvider;
        _explorerClientProvider = explorerClientProvider;
    }

    public async Task<bool> HasConfirmedReceiverInputsAsync(string storeId, string cryptoCode, BTCPayNetwork network, CancellationToken cancellationToken)
    {
        var confirmedCoins = await GetConfirmedReceiverCoinsAsync(storeId, cryptoCode, network, cancellationToken).ConfigureAwait(false);
        return confirmedCoins.Any();
    }

    internal async Task<PayjoinWalletContext> GetWalletContextAsync(
        string storeId, string cryptoCode, BTCPayNetwork network, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var store = await _storeRepository.FindStore(storeId).ConfigureAwait(false);
            var settings = store?.GetPaymentMethodConfig<DerivationSchemeSettings>(
                PaymentTypes.CHAIN.GetPaymentMethodId(cryptoCode), _handlers, true);
            var status = PayjoinWalletContext.GetProfileStatus(settings);
            if (status != PayjoinAvailabilityStatus.Active)
            {
                return new PayjoinWalletContext(status);
            }

            var client = _explorerClientProvider.GetExplorerClient(network);
            return client is null
                ? new PayjoinWalletContext(PayjoinAvailabilityStatus.TemporarilyUnavailable)
                : new PayjoinWalletContext(status, client, (StandardDerivationStrategyBase)settings!.AccountDerivation);
        }
        catch (Exception ex) when (PayjoinWalletContext.IsLookupUnavailable(ex, cancellationToken))
        {
            return new PayjoinWalletContext(PayjoinAvailabilityStatus.TemporarilyUnavailable);
        }
    }

    internal async Task<(PayjoinAvailabilityStatus Status, string? Reason)> GetNewSessionAvailabilityAsync(
        string storeId, string cryptoCode, BTCPayNetwork network, CancellationToken cancellationToken)
    {
        var context = await GetWalletContextAsync(storeId, cryptoCode, network, cancellationToken).ConfigureAwait(false);
        if (context.Status != PayjoinAvailabilityStatus.Active)
        {
            return (context.Status, context.Status == PayjoinAvailabilityStatus.MerchantRequirementsUnmet
                ? PayjoinUnavailableReasons.UnsupportedWalletProfile : PayjoinUnavailableReasons.WalletContextUnavailable);
        }

        try
        {
            if (!await context.Client!.IsTrackedAsync(new NBXplorer.Models.DerivationSchemeTrackedSource(context.AccountDerivation!), cancellationToken).ConfigureAwait(false))
            {
                return (PayjoinAvailabilityStatus.TemporarilyUnavailable, PayjoinUnavailableReasons.WalletContextUnavailable);
            }

            return await HasConfirmedReceiverInputsAsync(storeId, cryptoCode, network, cancellationToken).ConfigureAwait(false)
                ? (PayjoinAvailabilityStatus.Active, null)
                : (PayjoinAvailabilityStatus.TemporarilyUnavailable, PayjoinUnavailableReasons.NoConfirmedReceiverInputs);
        }
        catch (Exception ex) when (PayjoinWalletContext.IsLookupUnavailable(ex, cancellationToken))
        {
            return (PayjoinAvailabilityStatus.TemporarilyUnavailable, PayjoinUnavailableReasons.WalletContextUnavailable);
        }
    }

    public async Task<ReceivedCoin[]> GetConfirmedReceiverCoinsAsync(string storeId, string cryptoCode, BTCPayNetwork network, CancellationToken cancellationToken)
    {
        var store = await _storeRepository.FindStore(storeId).ConfigureAwait(false);
        if (store is null)
        {
            return [];
        }

        var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(cryptoCode);
        var derivationScheme = store.GetPaymentMethodConfig<DerivationSchemeSettings>(paymentMethodId, _handlers, true);
        if (derivationScheme is null)
        {
            return [];
        }

        var wallet = _walletProvider.GetWallet(network);
        if (wallet is null)
        {
            return [];
        }

        // TODO: Decide whether receiver contribution should ever fall back to unconfirmed merchant coins instead of requiring confirmed inputs before advertising payjoin.
        var coins = await wallet.GetUnspentCoins(derivationScheme.AccountDerivation, true, cancellationToken).ConfigureAwait(false);

        // Only advertise coins the receiver adapter can construct from the witness UTXO alone.
        // P2WPKH and Taproot key-path inputs have typed constructors with known expected weights.
        // P2WSH needs a spend-specific expected weight, which the adapter does not supply.
        return coins.Where(coin => IsSupportedReceiverCoin(coin.ScriptPubKey)).ToArray();
    }

    internal static bool IsSupportedReceiverCoin(Script scriptPubKey)
    {
        return scriptPubKey.IsScriptType(ScriptType.P2WPKH) ||
               scriptPubKey.IsScriptType(ScriptType.Taproot);
    }
}

internal sealed record PayjoinWalletContext(
    PayjoinAvailabilityStatus Status,
    ExplorerClient? Client = null,
    StandardDerivationStrategyBase? AccountDerivation = null)
{
    internal static PayjoinAvailabilityStatus GetProfileStatus(DerivationSchemeSettings? settings) =>
        settings is null ? PayjoinAvailabilityStatus.TemporarilyUnavailable :
        !settings.IsHotWallet || settings.Source is not ("NBXplorerGenerated" or "ImportedSeed") ? PayjoinAvailabilityStatus.MerchantRequirementsUnmet :
        settings.AccountDerivation is not StandardDerivationStrategyBase ? PayjoinAvailabilityStatus.TemporarilyUnavailable :
        PayjoinAvailabilityStatus.Active;

    internal static bool IsLookupUnavailable(Exception error, CancellationToken cancellationToken) =>
        error is HttpRequestException or IOException or NBXplorer.Models.NBXplorerException or NBitcoin.RPC.RPCException or
            Newtonsoft.Json.JsonException or FormatException or DbException ||
        error is OperationCanceledException && !cancellationToken.IsCancellationRequested;
}
