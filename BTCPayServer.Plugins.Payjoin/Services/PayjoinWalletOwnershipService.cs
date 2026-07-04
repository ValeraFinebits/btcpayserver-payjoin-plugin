using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Wallets;
using NBitcoin;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal interface IPayjoinWalletOwnershipService
{
    /// <summary>
    /// Resolves everything the synchronous rust-payjoin ownership callbacks will need ahead of time,
    /// so the callbacks themselves are pure in-memory lookups with no I/O on the protocol path.
    /// <paramref name="candidateOutputScripts"/> are the original transaction's output scripts, which
    /// are the only receiver-ownable scripts that can appear without funding an existing coin.
    /// </summary>
    Task<PayjoinScriptOwnershipResolver> CreateResolverAsync(
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
    private readonly BTCPayWalletProvider _walletProvider;
    private readonly IPayjoinStoreSettingsRepository _storeSettingsRepository;

    public PayjoinWalletOwnershipService(
        BTCPayNetworkProvider networkProvider,
        StoreRepository storeRepository,
        PaymentMethodHandlerDictionary handlers,
        ExplorerClientProvider explorerClientProvider,
        BTCPayWalletProvider walletProvider,
        IPayjoinStoreSettingsRepository storeSettingsRepository)
    {
        _networkProvider = networkProvider;
        _storeRepository = storeRepository;
        _handlers = handlers;
        _explorerClientProvider = explorerClientProvider;
        _walletProvider = walletProvider;
        _storeSettingsRepository = storeSettingsRepository;
    }

    public async Task<PayjoinScriptOwnershipResolver> CreateResolverAsync(
        string storeId,
        byte[] receiverScript,
        IReadOnlyCollection<byte[]> candidateOutputScripts,
        CancellationToken cancellationToken)
    {
        var network = _networkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode)
            ?? throw new InvalidOperationException($"Network '{PayjoinConstants.BitcoinCode}' is not available.");
        var store = await _storeRepository.FindStore(storeId).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Store {storeId} not found");
        var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(PayjoinConstants.BitcoinCode);
        var derivationScheme = store.GetPaymentMethodConfig<DerivationSchemeSettings>(paymentMethodId, _handlers, true)
            ?? throw new InvalidOperationException($"Derivation scheme not configured for {PayjoinConstants.BitcoinCode}");
        var wallet = _walletProvider.GetWallet(network)
            ?? throw new InvalidOperationException($"Wallet for {PayjoinConstants.BitcoinCode} is not available.");
        var client = _explorerClientProvider.GetExplorerClient(network);
        var coldWalletDerivation = await TryParseColdWalletDerivationAsync(storeId, network).ConfigureAwait(false);

        // Inputs: a receiver-owned input a sender can actually spend must reference one of the wallets'
        // existing coins (confirmed or in the mempool), so the coin scripts answer the inputs-not-owned
        // check without any per-script lookups. An input fabricated over a nonexistent outpoint with a
        // receiver script is not broadcastable and never signed (signing is scoped to the receiver's
        // contributed inputs), so it carries no ownership risk this check must catch.
        var ownedScripts = new HashSet<Script>();
        await AddUnspentCoinScriptsAsync(wallet, derivationScheme.AccountDerivation, ownedScripts, cancellationToken).ConfigureAwait(false);
        if (coldWalletDerivation is not null)
        {
            await AddUnspentCoinScriptsAsync(wallet, coldWalletDerivation, ownedScripts, cancellationToken).ConfigureAwait(false);
        }

        // Outputs: the original transaction's output scripts are known before the checks run, so any
        // that pay unfunded receiver addresses are resolved here, in one batch, against the hot and
        // cold derivations. The callbacks then never leave memory.
        var receiverScriptPubKey = Script.FromBytesUnsafe(receiverScript);
        foreach (var candidate in candidateOutputScripts)
        {
            var script = Script.FromBytesUnsafe(candidate);
            if (script == receiverScriptPubKey || ownedScripts.Contains(script))
            {
                continue;
            }

            if (await IsWalletScriptAsync(client, derivationScheme.AccountDerivation, coldWalletDerivation, script).ConfigureAwait(false))
            {
                ownedScripts.Add(script);
            }
        }

        return new PayjoinScriptOwnershipResolver(receiverScript, ownedScripts);
    }

    private static async Task AddUnspentCoinScriptsAsync(
        BTCPayWallet wallet,
        DerivationStrategyBase derivation,
        HashSet<Script> ownedScripts,
        CancellationToken cancellationToken)
    {
        var coins = await wallet.GetUnspentCoins(derivation, false, cancellationToken).ConfigureAwait(false);
        foreach (var coin in coins)
        {
            ownedScripts.Add(coin.ScriptPubKey);
        }
    }

    private static async Task<bool> IsWalletScriptAsync(
        ExplorerClient client,
        DerivationStrategyBase accountDerivation,
        DerivationStrategyBase? coldWalletDerivation,
        Script script)
    {
        var keyInformation = await client.GetKeyInformationAsync(accountDerivation, script).ConfigureAwait(false);
        if (keyInformation is not null)
        {
            return true;
        }

        if (coldWalletDerivation is null)
        {
            return false;
        }

        var coldKeyInformation = await client.GetKeyInformationAsync(coldWalletDerivation, script).ConfigureAwait(false);
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
}

/// <summary>
/// Answers script ownership for one proposal from data resolved ahead of the rust-payjoin callbacks:
/// the invoice's receiver script, the hot and cold wallets' current coin scripts, and any of the
/// original transaction's output scripts that resolved to a wallet address. Pure in-memory.
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
