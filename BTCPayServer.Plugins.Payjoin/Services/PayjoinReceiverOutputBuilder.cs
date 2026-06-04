using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PayjoinTxOut = Payjoin.TxOut;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal sealed class PayjoinReceiverOutputBuilder : IPayjoinReceiverOutputBuilder
{
    internal sealed class OutputReplacement
    {
        internal OutputReplacement(PayjoinTxOut[] replacementOutputs, byte[] settlementScript)
        {
            ReplacementOutputs = replacementOutputs;
            SettlementScript = settlementScript;
        }

        internal PayjoinTxOut[] ReplacementOutputs { get; }

        internal byte[] SettlementScript { get; }
    }

    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly StoreRepository _storeRepository;
    private readonly InvoiceRepository _invoiceRepository;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly ExplorerClientProvider _explorerClientProvider;
    private readonly IPayjoinStoreSettingsRepository _storeSettingsRepository;

    public PayjoinReceiverOutputBuilder(
        BTCPayNetworkProvider networkProvider,
        StoreRepository storeRepository,
        InvoiceRepository invoiceRepository,
        PaymentMethodHandlerDictionary handlers,
        ExplorerClientProvider explorerClientProvider,
        IPayjoinStoreSettingsRepository storeSettingsRepository)
    {
        _networkProvider = networkProvider;
        _storeRepository = storeRepository;
        _invoiceRepository = invoiceRepository;
        _handlers = handlers;
        _explorerClientProvider = explorerClientProvider;
        _storeSettingsRepository = storeSettingsRepository;
    }

    public async Task<OutputReplacement?> TryCreateSettlementOutputsAsync(
        string storeId,
        string invoiceId,
        byte[] receiverScript,
        bool preserveReceiverScript,
        CancellationToken cancellationToken)
    {
        // TODO: Add a rust-payjoin / payjoin-ffi API for reading the receiver amount from the proposal or original PSBT data.
        // TODO: Stop deriving the settlement amount from live invoice accounting state; replay should use an immutable proposal/session value.
        // TODO: Validate that the proposal-derived receiver amount matches the expected invoice amount before building replacement outputs.
        var settlementScript = preserveReceiverScript
            ? receiverScript
            : await GetSettlementScriptAsync(storeId, receiverScript, cancellationToken).ConfigureAwait(false);
        if (settlementScript is null)
        {
            return null;
        }

        var exactPaymentAmountSats = await TryGetExactPaymentAmountSatsAsync(invoiceId).ConfigureAwait(false);
        if (exactPaymentAmountSats is null)
        {
            return null;
        }

        return CreateSettlementOutputs(exactPaymentAmountSats.Value, settlementScript);
    }

    internal async Task<ulong?> TryGetExactPaymentAmountSatsAsync(string invoiceId)
    {
        var invoice = await _invoiceRepository.GetInvoice(invoiceId).ConfigureAwait(false);
        var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(PayjoinConstants.BitcoinCode);
        var prompt = invoice?.GetPaymentPrompt(paymentMethodId);
        if (prompt is null)
        {
            return null;
        }

        var due = prompt.Calculate().Due;
        if (due <= 0m)
        {
            return null;
        }

        var dueSats = Money.Coins(due).Satoshi;
        if (dueSats <= 0)
        {
            return null;
        }

        return checked((ulong)dueSats);
    }

    internal static OutputReplacement CreateSettlementOutputs(
        ulong exactPaymentAmountSats,
        byte[] settlementScript)
    {
        return new OutputReplacement(
            new[]
            {
                new PayjoinTxOut(exactPaymentAmountSats, settlementScript)
            },
            settlementScript);
    }

    private async Task<byte[]?> GetSettlementScriptAsync(
        string storeId,
        byte[] receiverScript,
        CancellationToken cancellationToken)
    {
        var network = _networkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode);
        if (network is null)
        {
            return null;
        }

        var client = _explorerClientProvider.GetExplorerClient(network);

        var coldWalletDerivation = await TryParseColdWalletDerivationAsync(storeId, network).ConfigureAwait(false);
        if (coldWalletDerivation is not null)
        {
            var coldChangeAddress = await client.GetUnusedAsync(coldWalletDerivation, DerivationFeature.Change, 0, true, cancellationToken).ConfigureAwait(false);
            var coldChangeScript = coldChangeAddress?.ScriptPubKey?.ToBytes();
            if (coldChangeScript is not null && coldChangeScript.Length > 0 && !coldChangeScript.SequenceEqual(receiverScript))
            {
                return coldChangeScript;
            }
        }

        var store = await _storeRepository.FindStore(storeId).ConfigureAwait(false);
        if (store is null)
        {
            return null;
        }

        var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(PayjoinConstants.BitcoinCode);
        var derivationScheme = store.GetPaymentMethodConfig<DerivationSchemeSettings>(paymentMethodId, _handlers, true);
        if (derivationScheme is null)
        {
            return null;
        }

        var changeAddress = await client.GetUnusedAsync(derivationScheme.AccountDerivation, DerivationFeature.Change, 0, true, cancellationToken).ConfigureAwait(false);
        var generatedReceiverChangeScriptPubKey = changeAddress?.ScriptPubKey;
        if (generatedReceiverChangeScriptPubKey is null)
        {
            return null;
        }

        var generatedReceiverChangeScript = generatedReceiverChangeScriptPubKey.ToBytes();
        if (generatedReceiverChangeScript.SequenceEqual(receiverScript))
        {
            return null;
        }

        return generatedReceiverChangeScript;
    }

    private async Task<DerivationStrategyBase?> TryParseColdWalletDerivationAsync(string storeId, BTCPayNetwork network)
    {
        var storeSettings = await _storeSettingsRepository.GetAsync(storeId).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(storeSettings.ColdWalletDerivationScheme))
        {
            return null;
        }

        try
        {
            return DerivationSchemeHelper.Parse(storeSettings.ColdWalletDerivationScheme, network).AccountDerivation;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
