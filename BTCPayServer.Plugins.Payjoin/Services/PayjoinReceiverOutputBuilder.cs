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
    internal sealed record SettlementDestination(byte[] Script, KeyPath? KeyPath);

    internal enum OutputBuildStatus
    {
        Ready,
        RetryableFailure,
        InvalidDestination
    }

    internal sealed record OutputBuildResult(OutputBuildStatus Status, OutputReplacement? Outputs = null);

    internal sealed class OutputReplacement
    {
        internal OutputReplacement(PayjoinTxOut[] replacementOutputs, byte[] settlementScript, ulong settlementAmountSats, KeyPath? settlementKeyPath)
        {
            ReplacementOutputs = replacementOutputs;
            SettlementScript = settlementScript;
            SettlementAmountSats = settlementAmountSats;
            SettlementKeyPath = settlementKeyPath;
        }

        internal PayjoinTxOut[] ReplacementOutputs { get; }

        internal byte[] SettlementScript { get; }

        internal ulong SettlementAmountSats { get; }

        internal KeyPath? SettlementKeyPath { get; }
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

    public async Task<OutputBuildResult> TryCreateSettlementOutputsAsync(
        string storeId,
        string invoiceId,
        byte[] receiverScript,
        bool preserveReceiverScript,
        long? pinnedSettlementAmountSats,
        CancellationToken cancellationToken)
    {
        if (pinnedSettlementAmountSats is not > 0) return new OutputBuildResult(OutputBuildStatus.InvalidDestination);
        (SettlementDestination? Destination, OutputBuildStatus Status) destinationResult;
        if (preserveReceiverScript)
        {
            destinationResult = await GetReceiverDestinationAsync(invoiceId, receiverScript).ConfigureAwait(false);
        }
        else
        {
            destinationResult = await GetSettlementDestinationAsync(storeId, receiverScript, cancellationToken).ConfigureAwait(false);
        }

        if (destinationResult.Destination is not { } settlementDestination)
        {
            return new OutputBuildResult(destinationResult.Status);
        }

        // The processor supplies the verified session URI pin. Live invoice accounting can drift
        // between replays and cannot replace the amount committed with these outputs.
        var exactPaymentAmountSats = checked((ulong)pinnedSettlementAmountSats.Value);
        return new OutputBuildResult(
            OutputBuildStatus.Ready,
            CreateSettlementOutputs(exactPaymentAmountSats, settlementDestination.Script, settlementDestination.KeyPath));
    }


    internal static OutputReplacement CreateSettlementOutputs(
        ulong exactPaymentAmountSats,
        byte[] settlementScript,
        KeyPath? settlementKeyPath)
    {
        return new OutputReplacement(
            new[]
            {
                new PayjoinTxOut(exactPaymentAmountSats, settlementScript)
            },
            settlementScript,
            exactPaymentAmountSats,
            settlementKeyPath is { Indexes.Length: > 0 } ? settlementKeyPath : null);
    }

    private async Task<(SettlementDestination? Destination, OutputBuildStatus Status)> GetSettlementDestinationAsync(
        string storeId,
        byte[] receiverScript,
        CancellationToken cancellationToken)
    {
        var network = _networkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode);
        if (network is null)
        {
            return (null, OutputBuildStatus.InvalidDestination);
        }

        var client = _explorerClientProvider.GetExplorerClient(network);

        var coldWalletDerivation = await TryParseColdWalletDerivationAsync(storeId, network).ConfigureAwait(false);
        if (coldWalletDerivation is not null)
        {
            var coldChangeAddress = await client.GetUnusedAsync(coldWalletDerivation, DerivationFeature.Change, 0, true, cancellationToken).ConfigureAwait(false);
            var coldChangeScript = coldChangeAddress?.ScriptPubKey?.ToBytes();
            if (coldChangeScript is not null && coldChangeScript.Length > 0 &&
                !coldChangeScript.SequenceEqual(receiverScript))
            {
                return (new SettlementDestination(coldChangeScript, coldChangeAddress!.KeyPath), OutputBuildStatus.Ready);
            }
        }

        var store = await _storeRepository.FindStore(storeId).ConfigureAwait(false);
        if (store is null)
        {
            return (null, OutputBuildStatus.InvalidDestination);
        }

        var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(PayjoinConstants.BitcoinCode);
        var derivationScheme = store.GetPaymentMethodConfig<DerivationSchemeSettings>(paymentMethodId, _handlers, true);
        if (derivationScheme is null)
        {
            return (null, OutputBuildStatus.InvalidDestination);
        }

        var changeAddress = await client.GetUnusedAsync(derivationScheme.AccountDerivation, DerivationFeature.Change, 0, true, cancellationToken).ConfigureAwait(false);
        var generatedReceiverChangeScriptPubKey = changeAddress?.ScriptPubKey;
        if (generatedReceiverChangeScriptPubKey is null)
        {
            return (null, OutputBuildStatus.RetryableFailure);
        }

        var generatedReceiverChangeScript = generatedReceiverChangeScriptPubKey.ToBytes();
        if (generatedReceiverChangeScript.Length == 0 || generatedReceiverChangeScript.SequenceEqual(receiverScript))
        {
            return (null, OutputBuildStatus.InvalidDestination);
        }

        return (new SettlementDestination(generatedReceiverChangeScript, changeAddress!.KeyPath), OutputBuildStatus.Ready);
    }

    private async Task<(SettlementDestination? Destination, OutputBuildStatus Status)> GetReceiverDestinationAsync(string invoiceId, byte[] receiverScript)
    {
        var invoice = await _invoiceRepository.GetInvoice(invoiceId).ConfigureAwait(false);
        var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(PayjoinConstants.BitcoinCode);
        var prompt = invoice?.GetPaymentPrompt(paymentMethodId);
        var network = _networkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode);
        if (prompt is null || network is null ||
            _handlers.ParsePaymentPromptDetails(prompt) is not BitcoinPaymentPromptDetails details)
        {
            return (null, OutputBuildStatus.InvalidDestination);
        }

        try
        {
            var promptScript = BitcoinAddress.Create(prompt.Destination, network.NBitcoinNetwork).ScriptPubKey.ToBytes();
            return promptScript.SequenceEqual(receiverScript)
                ? (new SettlementDestination(receiverScript, details.KeyPath), OutputBuildStatus.Ready)
                : (null, OutputBuildStatus.InvalidDestination);
        }
        catch (FormatException)
        {
            return (null, OutputBuildStatus.InvalidDestination);
        }
    }

    private async Task<DerivationStrategyBase?> TryParseColdWalletDerivationAsync(string storeId, BTCPayNetwork network)
    {
        var storeSettings = await _storeSettingsRepository.GetAsync(storeId).ConfigureAwait(false);
        if (storeSettings is null || string.IsNullOrWhiteSpace(storeSettings.ColdWalletDerivationScheme))
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
