using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LookupPass = BTCPayServer.Plugins.Payjoin.Services.PayjoinOwnershipLookupStateStore.LookupPass;
using ProposalLookupState = BTCPayServer.Plugins.Payjoin.Services.PayjoinOwnershipLookupStateStore.ProposalLookupState;

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

    async Task<PayjoinOwnershipLookupResult> CreateResolverAsync(
        PayjoinReceiverSessionState session, PayjoinOriginalTransactionFacts originalTransaction,
        byte[] receiverScript, CancellationToken cancellationToken)
    {
        var inputs = await CreateInputResolverAsync(session.StoreId, originalTransaction.InputOutpoints,
            cancellationToken).ConfigureAwait(false);
        return PayjoinOwnershipLookupResult.Ready(new PayjoinOwnershipResolver(inputs, new PayjoinInvoiceOutputMatcher(receiverScript)));
    }
}

internal sealed record PayjoinOwnershipResolver(PayjoinInputOwnershipResolver Inputs, PayjoinInvoiceOutputMatcher Outputs);

internal sealed class PayjoinWalletOwnershipService : IPayjoinWalletOwnershipService
{
    internal const int MaxRequestsPerPass = 128;
    internal static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(10);

    private static readonly Action<ILogger, string, Exception?> LogLookupDeferred =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, nameof(LogLookupDeferred)),
            "Payjoin ownership lookup deferred for {InvoiceId}; the receiver session remains active.");
    private static readonly Action<ILogger, string, Exception?> LogLookupExhausted =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(2, nameof(LogLookupExhausted)),
            "Payjoin ownership lookup budget exhausted for {InvoiceId}; no further passes will run for this proposal.");
    private static readonly Action<ILogger, Exception?> LogLookupCapacityUnavailable =
        LoggerMessage.Define(LogLevel.Warning, new EventId(3, nameof(LogLookupCapacityUnavailable)),
            "Payjoin ownership lookup state capacity is unavailable; new proposals will retry on a later tick.");

    private readonly Func<string, Task<PayjoinWalletContext>> _getWalletContextAsync;
    private readonly Func<PayjoinOwnershipSessionKey, bool> _isSessionCurrent;
    private readonly ILogger<PayjoinWalletOwnershipService> _logger;
    private readonly PayjoinOwnershipLookupStateStore _states;
    private readonly TimeSpan _lookupTimeout;

    public PayjoinWalletOwnershipService(BTCPayNetworkProvider networkProvider, PayjoinAvailabilityService availabilityService,
        PayjoinOwnershipLookupStateStore states, PayjoinReceiverSessionStore sessionStore,
        ILogger<PayjoinWalletOwnershipService> logger)
        : this(storeId => GetWalletContextAsync(storeId, networkProvider, availabilityService),
            states, sessionStore.IsCurrentSession, logger)
    {
    }

    internal PayjoinWalletOwnershipService(Func<string, Task<PayjoinWalletContext>> getWalletContextAsync,
        PayjoinOwnershipLookupStateStore states, Func<PayjoinOwnershipSessionKey, bool> isSessionCurrent,
        ILogger<PayjoinWalletOwnershipService>? logger = null, TimeSpan? lookupTimeout = null)
    {
        _getWalletContextAsync = getWalletContextAsync;
        _states = states;
        _isSessionCurrent = isSessionCurrent;
        _logger = logger ?? NullLogger<PayjoinWalletOwnershipService>.Instance;
        _lookupTimeout = lookupTimeout ?? LookupTimeout;
    }

    public async Task<PayjoinInputOwnershipResolver> CreateInputResolverAsync(
        string storeId, IReadOnlyCollection<OutPoint> candidateInputOutpoints, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = await _getWalletContextAsync(storeId).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Status != Models.PayjoinAvailabilityStatus.Active || context.Client is null || context.AccountDerivation is null)
        {
            return new PayjoinInputOwnershipResolver([]);
        }

        return await CreateInputResolverAsync(context.Client!, context.AccountDerivation!,
            candidateInputOutpoints, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PayjoinOwnershipLookupResult> CreateResolverAsync(
        PayjoinReceiverSessionState session, PayjoinOriginalTransactionFacts originalTransaction,
        byte[] receiverScript, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = _states.TryEnter(session, out var pendingPass);
        if (pendingPass is null) return Unavailable(status);
        using var pass = pendingPass;
        if (!_isSessionCurrent(pass.Session) || _states.IsRetired(pass))
        {
            _states.Forget(pass.Session);
            return Unavailable(PayjoinOwnershipLookupStatus.Exhausted);
        }

        PayjoinWalletContext wallet;
        try
        {
            wallet = await _getWalletContextAsync(session.StoreId).ConfigureAwait(false);
        }
        catch (Exception ex) when (PayjoinWalletContext.IsLookupUnavailable(ex, cancellationToken))
        {
            return Unavailable(PayjoinOwnershipLookupStatus.Deferred);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (wallet.Status != Models.PayjoinAvailabilityStatus.Active || wallet.Client is null || wallet.AccountDerivation is null)
            return Unavailable(PayjoinOwnershipLookupStatus.Deferred);

        var key = new PayjoinOwnershipProposalKey(pass.Session, session.PayjoinUri,
            originalTransaction.TransactionId, wallet.AccountDerivation.ToString());
        status = _states.TryBegin(pass, key, out var state, out var newlyExhausted);
        if (newlyExhausted) LogLookupExhausted(_logger, session.InvoiceId, null);
        if (state is null) return Unavailable(status);

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_lookupTimeout);
            var inputs = await CreateInputResolverAsync(wallet.Client, wallet.AccountDerivation,
                originalTransaction.InputOutpoints, deadline.Token, pass, state).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (_states.IsRetired(pass) || !_isSessionCurrent(pass.Session))
            {
                _states.Forget(pass.Session);
                return Unavailable(PayjoinOwnershipLookupStatus.Exhausted);
            }
            if (originalTransaction.InputOutpoints.Any(input => inputs.GetOwnership(input) == PayjoinInputOwnership.Unknown))
                return Fail(pass, session.InvoiceId, new InvalidOperationException("Input ownership evidence is incomplete."));
            return PayjoinOwnershipLookupResult.Ready(new PayjoinOwnershipResolver(inputs, new PayjoinInvoiceOutputMatcher(receiverScript)));
        }
        catch (LookupBudgetExceededException ex)
        {
            return Fail(pass, session.InvoiceId, ex, budgetExceeded: true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail(pass, session.InvoiceId, new TimeoutException("The ownership lookup pass deadline elapsed."));
        }
        catch (Exception ex) when (PayjoinWalletContext.IsLookupUnavailable(ex, cancellationToken))
        {
            return Fail(pass, session.InvoiceId, ex);
        }
    }

    private static Task<PayjoinWalletContext> GetWalletContextAsync(string storeId,
        BTCPayNetworkProvider networkProvider, PayjoinAvailabilityService availabilityService)
    {
        var network = networkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode);
        return network is null
            ? Task.FromResult(new PayjoinWalletContext(Models.PayjoinAvailabilityStatus.TemporarilyUnavailable))
            : availabilityService.GetWalletContextAsync(storeId, PayjoinConstants.BitcoinCode, network, CancellationToken.None);
    }

    internal static async Task<PayjoinInputOwnershipResolver> CreateInputResolverAsync(
        ExplorerClient client,
        DerivationStrategyBase accountDerivation,
        IReadOnlyCollection<OutPoint> candidateInputOutpoints,
        CancellationToken cancellationToken,
        LookupPass? pass = null,
        ProposalLookupState? state = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var inputs = candidateInputOutpoints.Distinct().ToDictionary(input => input,
            input => state?.OwnedInputs.Contains(input) == true ? PayjoinInputOwnership.Owned : PayjoinInputOwnership.Unknown);
        if (inputs.Count == 0) return new PayjoinInputOwnershipResolver(inputs);
        var scriptOwnership = new Dictionary<Script, PayjoinInputOwnership>();
        try
        {
            // Registration is necessary for a negative lookup. Completeness of address registration
            // and exclusive BTCPay use are operating requirements, not inferred from this endpoint.
            if (!await QueryAsync(pass, token => client.IsTrackedAsync(
                new NBXplorer.Models.DerivationSchemeTrackedSource(accountDerivation), token), cancellationToken).ConfigureAwait(false))
            {
                return new PayjoinInputOwnershipResolver(inputs);
            }
        }
        catch (Exception ex) when (PayjoinWalletContext.IsLookupUnavailable(ex, cancellationToken))
        {
            return new PayjoinInputOwnershipResolver(inputs);
        }

        foreach (var transactionInputs in inputs.Keys.GroupBy(outpoint => outpoint.Hash))
        {
            if (transactionInputs.All(input => inputs[input] == PayjoinInputOwnership.Owned)) continue;
            // Derivation-scoped history can omit funding received before tracking began, even
            // after synchronization. Authenticate the exact prevout before looking up its script;
            // sender-supplied witness UTXO data is not ownership evidence.
            Transaction? transaction = null;
            if (state?.FundingTransactions.TryGetValue(transactionInputs.Key, out transaction) != true)
            {
                NBXplorer.Models.TransactionResult? funding;
                try
                {
                    funding = await QueryAsync(pass, token => client.GetTransactionAsync(transactionInputs.Key, token),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (PayjoinWalletContext.IsLookupUnavailable(ex, cancellationToken))
                {
                    continue;
                }
                transaction = funding?.Transaction;
                // Core-only results can omit metadata; contradictory hashes remain Unknown.
                if ((funding?.TransactionHash is { } fundingId && fundingId != transactionInputs.Key) ||
                    (transaction is not null && transaction.GetHash() != transactionInputs.Key)) continue;
                if (transaction is not null && state is not null)
                    state.FundingTransactions[transactionInputs.Key] = transaction;
            }

            foreach (var input in transactionInputs)
            {
                if (inputs[input] == PayjoinInputOwnership.Owned) continue;
                try
                {
                    var prevout = transaction is null
                        ? await TryGetUnspentOutputAsync(client, input, pass, cancellationToken).ConfigureAwait(false)
                        : input.N < (uint)transaction.Outputs.Count ? transaction.Outputs[(int)input.N] : null;
                    if (prevout is null)
                    {
                        continue;
                    }

                    if (state?.OwnedScripts.Contains(prevout.ScriptPubKey) == true)
                    {
                        inputs[input] = PayjoinInputOwnership.Owned;
                        state.OwnedInputs.Add(input);
                        continue;
                    }
                    if (!scriptOwnership.TryGetValue(prevout.ScriptPubKey, out var ownership))
                    {
                        try
                        {
                            ownership = await GetScriptOwnershipAsync(client, accountDerivation,
                                prevout.ScriptPubKey, pass, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (PayjoinWalletContext.IsLookupUnavailable(ex, cancellationToken))
                        {
                            ownership = PayjoinInputOwnership.Unknown;
                        }
                        scriptOwnership.Add(prevout.ScriptPubKey, ownership);
                    }

                    inputs[input] = ownership;
                    if (ownership == PayjoinInputOwnership.Owned && state is not null)
                    {
                        state.OwnedInputs.Add(input);
                        state.OwnedScripts.Add(prevout.ScriptPubKey);
                    }
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
        LookupPass? pass,
        CancellationToken cancellationToken)
    {
        if (input.N > int.MaxValue)
        {
            return null;
        }

        // Ownership does not depend on spendability: include confirmed outputs spent
        // in the mempool, then check for outputs funded by unconfirmed transactions.
        var output = await QueryAsync(pass, token => client.RPCClient.GetTxOutAsync(input.Hash, (int)input.N,
            includeMempool: false, token), cancellationToken).ConfigureAwait(false)
            ?? await QueryAsync(pass, token => client.RPCClient.GetTxOutAsync(input.Hash, (int)input.N,
                includeMempool: true, token), cancellationToken).ConfigureAwait(false);
        return output?.TxOut;
    }

    private static async Task<PayjoinInputOwnership> GetScriptOwnershipAsync(
        ExplorerClient client, DerivationStrategyBase accountDerivation, Script script, LookupPass? pass,
        CancellationToken cancellationToken)
    {
        var records = await QueryAsync(pass, token => client.GetKeyInformationsAsync(script, token), cancellationToken).ConfigureAwait(false);
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

    private static Task<T> QueryAsync<T>(LookupPass? pass, Func<CancellationToken, Task<T>> query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pass is not null)
        {
            if (pass.RequestCount >= MaxRequestsPerPass) throw new LookupBudgetExceededException();
            pass.RequestCount++;
        }
        return query(cancellationToken);
    }

    private PayjoinOwnershipLookupResult Fail(LookupPass pass, string invoiceId, Exception exception, bool budgetExceeded = false)
    {
        var status = _states.Fail(pass, budgetExceeded);
        if (!_states.IsRetired(pass))
        {
            if (status == PayjoinOwnershipLookupStatus.Exhausted) LogLookupExhausted(_logger, invoiceId, exception);
            else LogLookupDeferred(_logger, invoiceId, exception);
        }
        return Unavailable(status);
    }

    private PayjoinOwnershipLookupResult Unavailable(PayjoinOwnershipLookupStatus status)
    {
        if (status == PayjoinOwnershipLookupStatus.CapacityUnavailable && _states.ShouldReportCapacity())
            LogLookupCapacityUnavailable(_logger, null);
        return PayjoinOwnershipLookupResult.Unavailable(status);
    }

    [SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Private control-flow signal caught inside CreateResolverAsync.")]
    [SuppressMessage("Design", "CA1064:Exceptions should be public", Justification = "Private control-flow signal caught inside CreateResolverAsync.")]
    private sealed class LookupBudgetExceededException : Exception
    {
        internal LookupBudgetExceededException() : base($"The ownership lookup pass requires more than {MaxRequestsPerPass} SDK operations.") { }
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
