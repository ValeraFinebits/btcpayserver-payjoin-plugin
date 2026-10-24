using NBitcoin;
using NBXplorer;
using NBXplorer.DerivationStrategy;
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
    /// authenticated funding transactions and wallet script ownership. The resulting rust-payjoin
    /// callback is a pure in-memory lookup and must not run while any input is Unknown.
    /// </summary>
    Task<PayjoinInputOwnershipResolver> CreateInputResolverAsync(
        string storeId,
        IReadOnlyCollection<OutPoint> candidateInputOutpoints,
        CancellationToken cancellationToken);


}

internal sealed class PayjoinWalletOwnershipService : IPayjoinWalletOwnershipService
{
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly PayjoinAvailabilityService _availabilityService;

    public PayjoinWalletOwnershipService(BTCPayNetworkProvider networkProvider, PayjoinAvailabilityService availabilityService)
    {
        _networkProvider = networkProvider;
        _availabilityService = availabilityService;
    }

    public async Task<PayjoinInputOwnershipResolver> CreateInputResolverAsync(
        string storeId, IReadOnlyCollection<OutPoint> candidateInputOutpoints, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var network = _networkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode);
        var context = network is null ? null : await _availabilityService.GetWalletContextAsync(
            storeId, PayjoinConstants.BitcoinCode, network, cancellationToken).ConfigureAwait(false);
        if (context?.Status != Models.PayjoinAvailabilityStatus.Active)
        {
            return new PayjoinInputOwnershipResolver([]);
        }

        return await CreateInputResolverAsync(context.Client!, context.AccountDerivation!,
            candidateInputOutpoints, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<PayjoinInputOwnershipResolver> CreateInputResolverAsync(
        ExplorerClient client,
        DerivationStrategyBase accountDerivation,
        IReadOnlyCollection<OutPoint> candidateInputOutpoints,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var inputs = candidateInputOutpoints.Distinct().ToDictionary(input => input, _ => PayjoinInputOwnership.Unknown);
        var scriptOwnership = new Dictionary<Script, PayjoinInputOwnership>();
        try
        {
            // Registration is necessary for a negative lookup. Completeness of address registration
            // and exclusive BTCPay use are operating requirements, not inferred from this endpoint.
            if (!await client.IsTrackedAsync(new NBXplorer.Models.DerivationSchemeTrackedSource(accountDerivation), cancellationToken).ConfigureAwait(false))
            {
                return new PayjoinInputOwnershipResolver(inputs);
            }
        }
        catch (Exception ex) when (PayjoinWalletContext.IsLookupUnavailable(ex, cancellationToken))
        {
            return new PayjoinInputOwnershipResolver(inputs);
        }

        // TODO(security): Charge distinct funding txids to a proposal-wide NBXplorer lookup budget with a deadline, limiter, and cache.
        foreach (var transactionInputs in inputs.Keys.GroupBy(outpoint => outpoint.Hash))
        {
            // Derivation-scoped history can omit funding received before tracking began, even
            // after synchronization. Authenticate the exact prevout before looking up its script;
            // sender-supplied witness UTXO data is not ownership evidence.
            NBXplorer.Models.TransactionResult? funding;
            try
            {
                funding = await client.GetTransactionAsync(transactionInputs.Key, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (PayjoinWalletContext.IsLookupUnavailable(ex, cancellationToken))
            {
                continue;
            }
            var transaction = funding?.Transaction;
            // Core-only results may omit NBXplorer's metadata txid. Contradictory evidence
            // must stay Unknown instead of being treated as an absent transaction.
            if ((funding?.TransactionHash is { } fundingId && fundingId != transactionInputs.Key) ||
                (transaction is not null && transaction.GetHash() != transactionInputs.Key))
            {
                continue;
            }

            foreach (var input in transactionInputs)
            {
                try
                {
                    var prevout = transaction is null
                        ? await TryGetUnspentOutputAsync(client, input, cancellationToken).ConfigureAwait(false)
                        : input.N < (uint)transaction.Outputs.Count ? transaction.Outputs[(int)input.N] : null;
                    if (prevout is null)
                    {
                        continue;
                    }

                    if (!scriptOwnership.TryGetValue(prevout.ScriptPubKey, out var ownership))
                    {
                        try
                        {
                            ownership = await GetScriptOwnershipAsync(client, accountDerivation,
                                prevout.ScriptPubKey, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (PayjoinWalletContext.IsLookupUnavailable(ex, cancellationToken))
                        {
                            ownership = PayjoinInputOwnership.Unknown;
                        }
                        scriptOwnership.Add(prevout.ScriptPubKey, ownership);
                    }

                    inputs[input] = ownership;
                }
                catch (Exception ex) when (PayjoinWalletContext.IsLookupUnavailable(ex, cancellationToken))
                {
                    // Retry unavailable evidence on the next normal session pass.
                }
            }
        }

        return new PayjoinInputOwnershipResolver(inputs);
    }

    private static async Task<TxOut?> TryGetUnspentOutputAsync(
        ExplorerClient client,
        OutPoint input,
        CancellationToken cancellationToken)
    {
        if (input.N > int.MaxValue)
        {
            return null;
        }

        // Ownership does not depend on spendability: include confirmed outputs spent
        // in the mempool, then check for outputs funded by unconfirmed transactions.
        var output = await client.RPCClient.GetTxOutAsync(input.Hash, (int)input.N,
            includeMempool: false, cancellationToken).ConfigureAwait(false)
            ?? await client.RPCClient.GetTxOutAsync(input.Hash, (int)input.N,
                includeMempool: true, cancellationToken).ConfigureAwait(false);
        return output?.TxOut;
    }

    private static async Task<PayjoinInputOwnership> GetScriptOwnershipAsync(
        ExplorerClient client, DerivationStrategyBase accountDerivation, Script script, CancellationToken cancellationToken)
    {
        var records = await client.GetKeyInformationsAsync(script, cancellationToken).ConfigureAwait(false);
        if (records is null)
        {
            return PayjoinInputOwnership.Unknown;
        }

        var owned = false;
        foreach (var record in records)
        {
            if (record is null || record.ScriptPubKey != script)
            {
                return PayjoinInputOwnership.Unknown;
            }

            if (record.DerivationStrategy?.ToString() != accountDerivation.ToString())
            {
                continue;
            }

            if (record.KeyPath is null || record.KeyPath.IsHardenedPath ||
                accountDerivation is not StandardDerivationStrategyBase standard ||
                standard.GetDerivation(record.KeyPath).ScriptPubKey != script)
            {
                return PayjoinInputOwnership.Unknown;
            }

            owned = true;
        }

        return owned ? PayjoinInputOwnership.Owned : PayjoinInputOwnership.Foreign;
    }
}

internal enum PayjoinInputOwnership
{
    Unknown,
    Owned,
    Foreign
}

internal sealed class PayjoinInputOwnershipResolver
{
    private readonly Dictionary<OutPoint, PayjoinInputOwnership> _inputs;

    internal PayjoinInputOwnershipResolver(Dictionary<OutPoint, PayjoinInputOwnership> inputs)
    {
        _inputs = inputs;
    }

    public PayjoinInputOwnership GetOwnership(OutPoint input) => _inputs.GetValueOrDefault(input, PayjoinInputOwnership.Unknown);

    public bool IsOwned(string transactionId, uint outputIndex)
    {
        if (!uint256.TryParse(transactionId, out var transactionHash))
        {
            throw new FormatException($"Invalid transaction id '{transactionId}'.");
        }

        return GetOwnership(new OutPoint(transactionHash, outputIndex)) switch
        {
            PayjoinInputOwnership.Owned => true,
            PayjoinInputOwnership.Foreign => false,
            _ => throw new InvalidOperationException("Input ownership must be resolved before invoking the native callback.")
        };
    }
}

/// <summary>Identifies only the invoice output for replacement. Pure in-memory.</summary>
internal sealed class PayjoinInvoiceOutputMatcher(byte[] receiverScript)
{
    public bool Matches(byte[] scriptBytes) => scriptBytes.AsSpan().SequenceEqual(receiverScript);
}
