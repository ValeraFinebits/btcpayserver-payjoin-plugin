using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
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
    internal sealed record SettlementDestination(byte[] Script, KeyPath KeyPath);

    internal sealed class OutputReplacement
    {
        internal OutputReplacement(PayjoinTxOut[] replacementOutputs, byte[] settlementScript, ulong settlementAmountSats, KeyPath settlementKeyPath)
        {
            ReplacementOutputs = replacementOutputs;
            SettlementScript = settlementScript;
            SettlementAmountSats = settlementAmountSats;
            SettlementKeyPath = settlementKeyPath;
        }

        internal PayjoinTxOut[] ReplacementOutputs { get; }

        internal byte[] SettlementScript { get; }

        internal ulong SettlementAmountSats { get; }

        internal KeyPath SettlementKeyPath { get; }
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
        long? pinnedSettlementAmountSats,
        CancellationToken cancellationToken)
    {
        // TODO: Add a rust-payjoin / payjoin-ffi API for reading the receiver amount from the proposal or
        // original PSBT data, so the settlement amount can be validated against what the sender actually
        // proposed instead of being derived on the receiver side.
        SettlementDestination? settlementDestination;
        if (preserveReceiverScript)
        {
            var receiverKeyPath = await TryGetReceiverKeyPathAsync(invoiceId, receiverScript).ConfigureAwait(false);
            settlementDestination = receiverKeyPath is null
                ? null
                : new SettlementDestination(receiverScript, receiverKeyPath);
        }
        else
        {
            settlementDestination = await GetSettlementDestinationAsync(storeId, receiverScript, cancellationToken).ConfigureAwait(false);
        }

        if (settlementDestination is null)
        {
            return null;
        }

        // The amount recorded on the accounting bridge when the sender's original arrived is preferred
        // over the live invoice accounting state: the latter can drift between replays (partial payments,
        // re-quotes), and whichever value ends up committed with the outputs is what crediting later uses.
        var exactPaymentAmountSats = pinnedSettlementAmountSats is > 0
            ? checked((ulong)pinnedSettlementAmountSats.Value)
            : await TryGetExactPaymentAmountSatsAsync(invoiceId).ConfigureAwait(false);
        if (exactPaymentAmountSats is null)
        {
            return null;
        }

        return CreateSettlementOutputs(exactPaymentAmountSats.Value, settlementDestination.Script, settlementDestination.KeyPath);
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
        byte[] settlementScript,
        KeyPath settlementKeyPath)
    {
        return new OutputReplacement(
            new[]
            {
                new PayjoinTxOut(exactPaymentAmountSats, settlementScript)
            },
            settlementScript,
            exactPaymentAmountSats,
            settlementKeyPath);
    }

    private async Task<SettlementDestination?> GetSettlementDestinationAsync(
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
            if (coldChangeScript is not null && coldChangeScript.Length > 0 &&
                coldChangeAddress!.KeyPath is { Indexes.Length: > 0 } coldChangeKeyPath &&
                !coldChangeScript.SequenceEqual(receiverScript))
            {
                return new SettlementDestination(coldChangeScript, coldChangeKeyPath);
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
        if (generatedReceiverChangeScriptPubKey is null ||
            changeAddress!.KeyPath is not { Indexes.Length: > 0 } changeKeyPath)
        {
            return null;
        }

        var generatedReceiverChangeScript = generatedReceiverChangeScriptPubKey.ToBytes();
        if (generatedReceiverChangeScript.SequenceEqual(receiverScript))
        {
            return null;
        }

        return new SettlementDestination(generatedReceiverChangeScript, changeKeyPath);
    }

    private async Task<KeyPath?> TryGetReceiverKeyPathAsync(string invoiceId, byte[] receiverScript)
    {
        var invoice = await _invoiceRepository.GetInvoice(invoiceId).ConfigureAwait(false);
        var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(PayjoinConstants.BitcoinCode);
        var prompt = invoice?.GetPaymentPrompt(paymentMethodId);
        var network = _networkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode);
        if (prompt is null || network is null ||
            _handlers.ParsePaymentPromptDetails(prompt) is not BitcoinPaymentPromptDetails details)
        {
            return null;
        }

        try
        {
            var promptScript = BitcoinAddress.Create(prompt.Destination, network.NBitcoinNetwork).ScriptPubKey.ToBytes();
            return promptScript.SequenceEqual(receiverScript) && details.KeyPath is { Indexes.Length: > 0 }
                ? details.KeyPath
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
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
