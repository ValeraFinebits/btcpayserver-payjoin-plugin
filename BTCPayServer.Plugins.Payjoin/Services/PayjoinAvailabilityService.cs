using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Wallets;
using NBitcoin;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

public sealed class PayjoinAvailabilityService
{
    private readonly StoreRepository _storeRepository;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly BTCPayWalletProvider _walletProvider;

    public PayjoinAvailabilityService(
        StoreRepository storeRepository,
        PaymentMethodHandlerDictionary handlers,
        BTCPayWalletProvider walletProvider)
    {
        _storeRepository = storeRepository;
        _handlers = handlers;
        _walletProvider = walletProvider;
    }

    public async Task<bool> HasConfirmedReceiverInputsAsync(string storeId, string cryptoCode, BTCPayNetwork network, CancellationToken cancellationToken)
    {
        var confirmedCoins = await GetConfirmedReceiverCoinsAsync(storeId, cryptoCode, network, cancellationToken).ConfigureAwait(false);
        return confirmedCoins.Any();
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

        // The receiver input path constructs PSBT inputs from the witness UTXO alone (no
        // non_witness_utxo, redeem or witness scripts), which is only valid for natively segwit
        // coins. Offering anything else produces inputs the library rejects, so candidates are
        // limited here - which also keeps checkout from advertising payjoin a legacy-funded wallet
        // cannot actually serve.
        return coins.Where(coin => IsSupportedReceiverCoin(coin.ScriptPubKey)).ToArray();
    }

    internal static bool IsSupportedReceiverCoin(Script scriptPubKey)
    {
        return scriptPubKey.IsScriptType(ScriptType.P2WPKH) ||
               scriptPubKey.IsScriptType(ScriptType.P2WSH) ||
               scriptPubKey.IsScriptType(ScriptType.Taproot);
    }
}
