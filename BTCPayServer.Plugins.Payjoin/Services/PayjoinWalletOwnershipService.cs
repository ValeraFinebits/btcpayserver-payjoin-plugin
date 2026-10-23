using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LookupPass = BTCPayServer.Plugins.Payjoin.Services.PayjoinOwnershipLookupStateStore.LookupPass;
using ProposalLookupState = BTCPayServer.Plugins.Payjoin.Services.PayjoinOwnershipLookupStateStore.ProposalLookupState;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal interface IPayjoinWalletOwnershipService
{
    /// <summary>
    /// Resolves both input and output ownership within one shared budget and deadline.
    /// Unavailable results retain the session without classifying the proposal as invalid.
    /// </summary>
    Task<PayjoinOwnershipLookupResult> CreateResolverAsync(
        PayjoinReceiverSessionState session,
        PayjoinOriginalTransactionFacts originalTransaction,
        byte[] receiverScript,
        CancellationToken cancellationToken);
}

internal sealed record PayjoinOwnershipResolver(
    PayjoinInputOwnershipResolver Inputs,
    PayjoinScriptOwnershipResolver Outputs);

internal sealed class PayjoinWalletOwnershipService : IPayjoinWalletOwnershipService
{
    // Each of the three admitted passes has its own SDK-operation budget and NBXplorer deadline.
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

    private readonly Func<string, Task<WalletContext>> _getWalletContextAsync;
    private readonly Func<PayjoinOwnershipSessionKey, bool> _isSessionCurrent;
    private readonly ILogger<PayjoinWalletOwnershipService> _logger;
    private readonly PayjoinOwnershipLookupStateStore _states;
    private readonly TimeSpan _lookupTimeout;

    public PayjoinWalletOwnershipService(
        BTCPayNetworkProvider networkProvider,
        StoreRepository storeRepository,
        PaymentMethodHandlerDictionary handlers,
        ExplorerClientProvider explorerClientProvider,
        IPayjoinStoreSettingsRepository storeSettingsRepository,
        PayjoinOwnershipLookupStateStore states,
        PayjoinReceiverSessionStore sessionStore,
        ILogger<PayjoinWalletOwnershipService> logger)
        : this(storeId => GetWalletContextAsync(storeId, networkProvider, storeRepository, handlers,
            explorerClientProvider, storeSettingsRepository), states, sessionStore.IsCurrentSession, logger)
    {
    }

    internal PayjoinWalletOwnershipService(
        Func<string, Task<WalletContext>> getWalletContextAsync,
        PayjoinOwnershipLookupStateStore states,
        Func<PayjoinOwnershipSessionKey, bool> isSessionCurrent,
        ILogger<PayjoinWalletOwnershipService>? logger = null,
        TimeSpan? lookupTimeout = null)
    {
        _getWalletContextAsync = getWalletContextAsync;
        _isSessionCurrent = isSessionCurrent;
        _logger = logger ?? NullLogger<PayjoinWalletOwnershipService>.Instance;
        _states = states;
        _lookupTimeout = lookupTimeout ?? LookupTimeout;
    }

    public async Task<PayjoinOwnershipLookupResult> CreateResolverAsync(
        PayjoinReceiverSessionState session,
        PayjoinOriginalTransactionFacts originalTransaction,
        byte[] receiverScript,
        CancellationToken cancellationToken)
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
        var wallet = await _getWalletContextAsync(session.StoreId).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var key = new PayjoinOwnershipProposalKey(pass.Session, session.PayjoinUri,
            originalTransaction.TransactionId, wallet.AccountDerivation.ToString(), wallet.ColdWalletDerivation?.ToString());
        status = _states.TryBegin(pass, key, out var state, out var newlyExhausted);
        if (newlyExhausted) LogLookupExhausted(_logger, session.InvoiceId, null);
        if (state is null) return Unavailable(status);

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_lookupTimeout);
            var resolver = await ResolveAsync(wallet, state, pass, originalTransaction, receiverScript, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            return _states.IsRetired(pass)
                ? Unavailable(PayjoinOwnershipLookupStatus.Exhausted)
                : PayjoinOwnershipLookupResult.Ready(resolver);
        }
        catch (LookupBudgetExceededException ex)
        {
            return Fail(pass, session.InvoiceId, ex, budgetExceeded: true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail(pass, session.InvoiceId, new TimeoutException("The ownership lookup pass deadline elapsed."));
        }
        catch (HttpRequestException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Fail(pass, session.InvoiceId, ex);
        }
        catch (NBXplorerException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Fail(pass, session.InvoiceId, ex);
        }
    }

    private static async Task<PayjoinOwnershipResolver> ResolveAsync(
        WalletContext wallet,
        ProposalLookupState state,
        LookupPass pass,
        PayjoinOriginalTransactionFacts originalTransaction,
        byte[] receiverScript,
        CancellationToken cancellationToken)
    {
        var ownedInputs = new HashSet<OutPoint>();
        foreach (var transactionInputs in originalTransaction.InputOutpoints.Distinct().GroupBy(outpoint => outpoint.Hash))
        {
            var hotOutputs = await GetMatchedOutputsAsync(wallet, state, pass, wallet.AccountDerivation,
                transactionInputs, cancellationToken).ConfigureAwait(false);
            AddMatchedInputs(transactionInputs, hotOutputs, ownedInputs);
            if (wallet.ColdWalletDerivation is not null)
            {
                var coldOutputs = await GetMatchedOutputsAsync(wallet, state, pass, wallet.ColdWalletDerivation,
                    transactionInputs, cancellationToken).ConfigureAwait(false);
                AddMatchedInputs(transactionInputs, coldOutputs, ownedInputs);
            }
        }

        var ownedScripts = new HashSet<Script>();
        var receiverScriptPubKey = Script.FromBytesUnsafe(receiverScript);
        foreach (var script in originalTransaction.OutputScripts.Select(Script.FromBytesUnsafe).Distinct())
        {
            if (script == receiverScriptPubKey) continue;
            if (state.OwnedScripts.Contains(script) ||
                await IsWalletScriptAsync(wallet, pass, script, cancellationToken).ConfigureAwait(false))
            {
                ownedScripts.Add(script);
                state.OwnedScripts.Add(script);
            }
        }

        return new PayjoinOwnershipResolver(new PayjoinInputOwnershipResolver(ownedInputs),
            new PayjoinScriptOwnershipResolver(receiverScript, ownedScripts));
    }

    private static async Task<uint[]?> GetMatchedOutputsAsync(
        WalletContext wallet, ProposalLookupState state, LookupPass pass, DerivationStrategyBase derivation,
        IGrouping<uint256, OutPoint> candidateInputs, CancellationToken cancellationToken)
    {
        var key = (derivation.ToString(), candidateInputs.Key);
        if (state.MatchedOutputs.TryGetValue(key, out var outputs) && candidateInputs.All(input => outputs.Contains(input.N)))
            return outputs;
        var transaction = await QueryAsync(pass,
            token => wallet.Client.GetTransactionAsync(derivation, candidateInputs.Key, token), cancellationToken).ConfigureAwait(false);
        var candidateIndexes = candidateInputs.Select(input => input.N).ToHashSet();
        var matched = transaction?.Outputs?.Select(output => checked((uint)output.Index))
            .Where(candidateIndexes.Contains).Distinct().ToArray();
        if (matched is { Length: > 0 })
        {
            outputs = outputs is null ? matched : outputs.Union(matched).ToArray();
            state.MatchedOutputs[key] = outputs;
        }
        return outputs;
    }

    private static async Task<bool> IsWalletScriptAsync(
        WalletContext wallet, LookupPass pass, Script script, CancellationToken cancellationToken)
    {
        var hot = await QueryAsync(pass,
            token => wallet.Client.GetKeyInformationAsync(wallet.AccountDerivation, script, token), cancellationToken).ConfigureAwait(false);
        if (hot is not null) return true;
        if (wallet.ColdWalletDerivation is null) return false;
        var cold = await QueryAsync(pass,
            token => wallet.Client.GetKeyInformationAsync(wallet.ColdWalletDerivation, script, token), cancellationToken).ConfigureAwait(false);
        return cold is not null;
    }

    private static Task<T> QueryAsync<T>(LookupPass pass, Func<CancellationToken, Task<T>> query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pass.RequestCount >= MaxRequestsPerPass) throw new LookupBudgetExceededException();
        pass.RequestCount++;
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

    [SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Private control-flow signal caught inside CreateResolverAsync; it never leaves this service.")]
    [SuppressMessage("Design", "CA1064:Exceptions should be public", Justification = "Private control-flow signal caught inside CreateResolverAsync; it never leaves this service.")]
    private sealed class LookupBudgetExceededException : Exception
    {
        internal LookupBudgetExceededException() : base($"The ownership lookup pass requires more than {MaxRequestsPerPass} SDK operations.") { }
    }

    private static async Task<WalletContext> GetWalletContextAsync(
        string storeId, BTCPayNetworkProvider networkProvider, StoreRepository storeRepository,
        PaymentMethodHandlerDictionary handlers, ExplorerClientProvider explorerClientProvider,
        IPayjoinStoreSettingsRepository storeSettingsRepository)
    {
        var network = networkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode)
            ?? throw new InvalidOperationException($"Network '{PayjoinConstants.BitcoinCode}' is not available.");
        var store = await storeRepository.FindStore(storeId).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Store {storeId} not found");
        var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(PayjoinConstants.BitcoinCode);
        var derivationScheme = store.GetPaymentMethodConfig<DerivationSchemeSettings>(paymentMethodId, handlers, true)
            ?? throw new InvalidOperationException($"Derivation scheme not configured for {PayjoinConstants.BitcoinCode}");
        var client = explorerClientProvider.GetExplorerClient(network);
        var coldWalletDerivation = await TryParseColdWalletDerivationAsync(storeId, network, storeSettingsRepository).ConfigureAwait(false);
        return new WalletContext(client, derivationScheme.AccountDerivation, coldWalletDerivation);
    }

    private static void AddMatchedInputs(
        IEnumerable<OutPoint> candidateInputs,
        uint[]? ownedOutputIndexes,
        HashSet<OutPoint> ownedInputs)
    {
        // TODO(security): Do not collapse an NBXplorer no-match into Foreign while wallet
        // ownership readiness is unverified. Represent it as Unknown and fail closed until
        // the relevant hot/cold derivation histories are known to be authoritative.
        if (ownedOutputIndexes is null)
        {
            return;
        }

        foreach (var input in candidateInputs)
        {
            if (ownedOutputIndexes.Contains(input.N))
            {
                ownedInputs.Add(input);
            }
        }
    }

    private static async Task<DerivationStrategyBase?> TryParseColdWalletDerivationAsync(
        string storeId, BTCPayNetwork network, IPayjoinStoreSettingsRepository storeSettingsRepository)
    {
        var storeSettings = await storeSettingsRepository.GetAsync(storeId).ConfigureAwait(false);
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

    internal sealed record WalletContext(
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
