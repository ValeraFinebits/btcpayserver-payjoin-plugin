using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using Payjoin;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal sealed class PayjoinReceiverOutputBuilder : IPayjoinReceiverOutputBuilder
{
    internal sealed class OutputReplacement
    {
        internal OutputReplacement(PlainTxOut[] exactPaymentOutputs, byte[] receiverChangeScript)
        {
            ExactPaymentOutputs = exactPaymentOutputs;
            ReceiverChangeScript = receiverChangeScript;
        }

        internal PlainTxOut[] ExactPaymentOutputs { get; }

        internal byte[] ReceiverChangeScript { get; }
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

    public async Task<OutputReplacement?> TryCreateExactPaymentOutputsAsync(
        string storeId,
        string invoiceId,
        byte[] receiverScript,
        CancellationToken cancellationToken)
    {
        // TODO: Add an explicit rust-payjoin / payjoin-ffi API for reading receiver outputs or original PSBT data.
        // TODO: Replace the invoice.Due fallback below with values read directly from the incoming payjoin proposal.
        // TODO: Validate that the invoice amount matches the receiver amount in the incoming proposal before building replacement outputs.
        var receiverChangeScript = await GetReceiverChangeScriptAsync(storeId, receiverScript, cancellationToken).ConfigureAwait(false);
        if (receiverChangeScript is null)
        {
            return null;
        }

        var exactPaymentAmountSats = await TryGetExactPaymentAmountSatsAsync(invoiceId).ConfigureAwait(false);
        if (exactPaymentAmountSats is null)
        {
            return null;
        }

        return CreateExactPaymentOutputs(exactPaymentAmountSats.Value, receiverScript, receiverChangeScript);
    }

    internal async Task<ulong?> TryGetExactPaymentAmountSatsAsync(string invoiceId)
    {
        var invoice = await _invoiceRepository.GetInvoice(invoiceId).ConfigureAwait(false);
        var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId("BTC");
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

    internal static OutputReplacement CreateExactPaymentOutputs(
        ulong exactPaymentAmountSats,
        byte[] receiverScript,
        byte[] receiverChangeScript)
    {
        return new OutputReplacement(
            new[]
            {
                new PlainTxOut(exactPaymentAmountSats, receiverScript),
                new PlainTxOut(0, receiverChangeScript)
            },
            receiverChangeScript);
    }

    private async Task<byte[]?> GetReceiverChangeScriptAsync(
        string storeId,
        byte[] receiverScript,
        CancellationToken cancellationToken)
    {
        var network = _networkProvider.GetNetwork<BTCPayNetwork>("BTC");
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

        var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId("BTC");
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
