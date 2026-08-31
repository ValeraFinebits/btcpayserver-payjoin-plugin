using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using NBitcoin;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal interface IPayjoinWalletOwnershipService
{
    /// <summary>
    /// Resolves ownership of the Original transaction's exact input outpoints from the wallet's
    /// tracked transaction history. The resulting rust-payjoin callback is a pure in-memory lookup.
    /// </summary>
    Task<PayjoinInputOwnershipResolver> CreateInputResolverAsync(
        string storeId,
        IReadOnlyCollection<OutPoint> candidateInputOutpoints,
        CancellationToken cancellationToken);

    Task<PayjoinScriptOwnershipResolver> CreateOutputResolverAsync(
        string storeId,
        byte[] receiverScript,
        IReadOnlyCollection<byte[]> candidateOutputScripts,
        CancellationToken cancellationToken);
}

internal sealed class PayjoinWalletOwnershipService : IPayjoinWalletOwnershipService
{
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly StoreRepository _storeRepository;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly ExplorerClientProvider _explorerClientProvider;
    private readonly IPayjoinStoreSettingsRepository _storeSettingsRepository;

    public PayjoinWalletOwnershipService(
        BTCPayNetworkProvider networkProvider,
        StoreRepository storeRepository,
        PaymentMethodHandlerDictionary handlers,
        ExplorerClientProvider explorerClientProvider,
        IPayjoinStoreSettingsRepository storeSettingsRepository)
    {
        _networkProvider = networkProvider;
        _storeRepository = storeRepository;
        _handlers = handlers;
        _explorerClientProvider = explorerClientProvider;
        _storeSettingsRepository = storeSettingsRepository;
    }

    public async Task<PayjoinInputOwnershipResolver> CreateInputResolverAsync(
        string storeId,
        IReadOnlyCollection<OutPoint> candidateInputOutpoints,
        CancellationToken cancellationToken)
    {
        var walletContext = await GetWalletContextAsync(storeId).ConfigureAwait(false);
        var ownedInputs = new HashSet<OutPoint>();

        // TODO(security): Charge distinct funding txids to a proposal-wide NBXplorer lookup budget with a deadline, limiter, and cache.
        foreach (var transactionInputs in candidateInputOutpoints.Distinct().GroupBy(outpoint => outpoint.Hash))
        {
            if (walletContext.ColdWalletDerivation is null)
            {
                var hotTransaction = await walletContext.Client.GetTransactionAsync(
                    walletContext.AccountDerivation,
                    transactionInputs.Key,
                    cancellationToken).ConfigureAwait(false);
                AddMatchedInputs(transactionInputs, hotTransaction?.Outputs, ownedInputs);
                continue;
            }

            var transactions = await Task.WhenAll(
                walletContext.Client.GetTransactionAsync(
                    walletContext.AccountDerivation,
                    transactionInputs.Key,
                    cancellationToken),
                walletContext.Client.GetTransactionAsync(
                    walletContext.ColdWalletDerivation,
                    transactionInputs.Key,
                    cancellationToken)).ConfigureAwait(false);
            AddMatchedInputs(transactionInputs, transactions[0]?.Outputs, ownedInputs);
            AddMatchedInputs(transactionInputs, transactions[1]?.Outputs, ownedInputs);
        }

        return new PayjoinInputOwnershipResolver(ownedInputs);
    }

    public async Task<PayjoinScriptOwnershipResolver> CreateOutputResolverAsync(
        string storeId,
        byte[] receiverScript,
        IReadOnlyCollection<byte[]> candidateOutputScripts,
        CancellationToken cancellationToken)
    {
        var walletContext = await GetWalletContextAsync(storeId).ConfigureAwait(false);
        var ownedScripts = new HashSet<Script>();

        var receiverScriptPubKey = Script.FromBytesUnsafe(receiverScript);
        // TODO(security): Charge distinct output scripts to the same proposal-wide NBXplorer lookup budget with a deadline, limiter, and cache.
        foreach (var script in candidateOutputScripts.Select(Script.FromBytesUnsafe).Distinct())
        {
            if (script == receiverScriptPubKey)
            {
                continue;
            }

            if (await IsWalletScriptAsync(
                    walletContext.Client,
                    walletContext.AccountDerivation,
                    walletContext.ColdWalletDerivation,
                    script,
                    cancellationToken).ConfigureAwait(false))
            {
                ownedScripts.Add(script);
            }
        }

        return new PayjoinScriptOwnershipResolver(receiverScript, ownedScripts);
    }

    private async Task<WalletContext> GetWalletContextAsync(string storeId)
    {
        var network = _networkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode)
            ?? throw new InvalidOperationException($"Network '{PayjoinConstants.BitcoinCode}' is not available.");
        var store = await _storeRepository.FindStore(storeId).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Store {storeId} not found");
        var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(PayjoinConstants.BitcoinCode);
        var derivationScheme = store.GetPaymentMethodConfig<DerivationSchemeSettings>(paymentMethodId, _handlers, true)
            ?? throw new InvalidOperationException($"Derivation scheme not configured for {PayjoinConstants.BitcoinCode}");
        var client = _explorerClientProvider.GetExplorerClient(network);
        var coldWalletDerivation = await TryParseColdWalletDerivationAsync(storeId, network).ConfigureAwait(false);
        return new WalletContext(client, derivationScheme.AccountDerivation, coldWalletDerivation);
    }

    private static void AddMatchedInputs(
        IEnumerable<OutPoint> candidateInputs,
        IEnumerable<MatchedOutput>? matchedOutputs,
        HashSet<OutPoint> ownedInputs)
    {
        // TODO(security): Do not collapse an NBXplorer no-match into Foreign while wallet
        // ownership readiness is unverified. Represent it as Unknown and fail closed until
        // the relevant hot/cold derivation histories are known to be authoritative.
        if (matchedOutputs is null)
        {
            return;
        }

        var ownedOutputIndexes = matchedOutputs.Select(output => checked((uint)output.Index)).ToHashSet();
        foreach (var input in candidateInputs)
        {
            if (ownedOutputIndexes.Contains(input.N))
            {
                ownedInputs.Add(input);
            }
        }
    }

    private static async Task<bool> IsWalletScriptAsync(
        ExplorerClient client,
        DerivationStrategyBase accountDerivation,
        DerivationStrategyBase? coldWalletDerivation,
        Script script,
        CancellationToken cancellationToken)
    {
        var keyInformation = await client.GetKeyInformationAsync(accountDerivation, script, cancellationToken).ConfigureAwait(false);
        if (keyInformation is not null)
        {
            return true;
        }

        if (coldWalletDerivation is null)
        {
            return false;
        }

        var coldKeyInformation = await client.GetKeyInformationAsync(coldWalletDerivation, script, cancellationToken).ConfigureAwait(false);
        return coldKeyInformation is not null;
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

    private sealed record WalletContext(
        ExplorerClient Client,
        DerivationStrategyBase AccountDerivation,
        DerivationStrategyBase? ColdWalletDerivation);
}

internal sealed class PayjoinInputOwnershipResolver
{
    private readonly HashSet<OutPoint> _ownedInputs;

    internal PayjoinInputOwnershipResolver(HashSet<OutPoint> ownedInputs)
    {
        _ownedInputs = ownedInputs;
    }

    public bool IsOwned(string transactionId, uint outputIndex)
    {
        if (!uint256.TryParse(transactionId, out var transactionHash))
        {
            throw new FormatException($"Invalid transaction id '{transactionId}'.");
        }

        return _ownedInputs.Contains(new OutPoint(transactionHash, outputIndex));
    }
}

/// <summary>
/// Answers output-script ownership for one proposal from scripts resolved ahead of the
/// rust-payjoin callback. Pure in-memory.
/// </summary>
internal sealed class PayjoinScriptOwnershipResolver
{
    private readonly byte[] _receiverScript;
    private readonly HashSet<Script> _ownedScripts;

    internal PayjoinScriptOwnershipResolver(byte[] receiverScript, HashSet<Script> ownedScripts)
    {
        _receiverScript = receiverScript;
        _ownedScripts = ownedScripts;
    }

    public bool IsOwned(byte[] scriptBytes)
    {
        if (scriptBytes.AsSpan().SequenceEqual(_receiverScript))
        {
            return true;
        }

        return _ownedScripts.Contains(Script.FromBytesUnsafe(scriptBytes));
    }
}
