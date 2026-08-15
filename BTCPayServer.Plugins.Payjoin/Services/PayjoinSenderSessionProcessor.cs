using BTCPayServer.Abstractions;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer;
using Payjoin;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SystemUri = System.Uri;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal interface IPayjoinSenderSessionProcessor
{
    Task ProcessTickAsync(CancellationToken stoppingToken);
}

/// <summary>
/// Drives every pending sender session one step per tick through the rust-payjoin sender state
/// machine: post the original PSBT, poll for the receiver's proposal, sign and broadcast the
/// proposal when it arrives, and broadcast the original transaction when the library moves the
/// session to its fallback state. Every transition persists to the session's event log first, so
/// a restart replays the log and resumes exactly where the previous run stopped.
/// </summary>
internal sealed class PayjoinSenderSessionProcessor : IPayjoinSenderSessionProcessor
{
    private static readonly Action<ILogger, string, Exception?> LogSenderSessionFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, nameof(LogSenderSessionFailed)),
            "Payjoin sender session {SenderSessionId} failed");
    private static readonly Action<ILogger, string, Exception?> LogSenderSessionTransient =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(2, nameof(LogSenderSessionTransient)),
            "Payjoin sender session {SenderSessionId} hit a transient error; it retries next tick");
    private static readonly Action<ILogger, string, string, Exception?> LogSenderSessionBroadcast =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(3, nameof(LogSenderSessionBroadcast)),
            "Payjoin sender session {SenderSessionId} broadcast {TransactionId}");
    private static readonly Action<ILogger, string, string, Exception?> LogProposalAwaitingSignature =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(5, nameof(LogProposalAwaitingSignature)),
            "Payjoin sender session {SenderSessionId} needs a second signature on pending transaction {PendingTransactionId}");
    private static readonly Action<ILogger, string, Exception?> LogSenderRelayUnavailable =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(4, nameof(LogSenderRelayUnavailable)),
            "Payjoin sender session {SenderSessionId} has no reachable OHTTP relay; it retries next tick");

    private readonly PayjoinSenderSessionStore _senderSessionStore;
    private readonly IPayjoinStoreSettingsRepository _storeSettingsRepository;
    private readonly IPayjoinReceiverRelayClient _relayClient;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly StoreRepository _storeRepository;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly ExplorerClientProvider _explorerClientProvider;
    private readonly PendingTransactionService _pendingTransactionService;
    private readonly ILogger<PayjoinSenderSessionProcessor> _logger;

    internal PayjoinSenderSessionProcessor(
        PayjoinSenderSessionStore senderSessionStore,
        IPayjoinStoreSettingsRepository storeSettingsRepository,
        IPayjoinReceiverRelayClient relayClient,
        BTCPayNetworkProvider networkProvider,
        StoreRepository storeRepository,
        PaymentMethodHandlerDictionary handlers,
        ExplorerClientProvider explorerClientProvider,
        PendingTransactionService pendingTransactionService,
        ILogger<PayjoinSenderSessionProcessor> logger)
    {
        _senderSessionStore = senderSessionStore;
        _storeSettingsRepository = storeSettingsRepository;
        _relayClient = relayClient;
        _networkProvider = networkProvider;
        _storeRepository = storeRepository;
        _handlers = handlers;
        _explorerClientProvider = explorerClientProvider;
        _pendingTransactionService = pendingTransactionService;
        _logger = logger;
    }

    public async Task ProcessTickAsync(CancellationToken stoppingToken)
    {
        foreach (var session in _senderSessionStore.GetPendingSessions())
        {
            stoppingToken.ThrowIfCancellationRequested();
            try
            {
                await ProcessSessionAsync(session, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (SenderPersistedException.Transient ex)
            {
                LogSenderSessionTransient(_logger, session.SenderSessionId, ex);
            }
            catch (SenderReplayException ex)
            {
                FailSession(session.SenderSessionId, $"sender session replay failed: {ex.Message}", ex);
            }
            catch (UniffiException ex)
            {
                FailSession(session.SenderSessionId, $"sender session failed: {ex.Message}", ex);
            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                LogSenderSessionTransient(_logger, session.SenderSessionId, ex);
            }
            catch (InvalidOperationException ex)
            {
                FailSession(session.SenderSessionId, ex.Message, ex);
            }
        }
    }

    private async Task ProcessSessionAsync(PayjoinSenderSessionState session, CancellationToken cancellationToken)
    {
        var network = _networkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode)
            ?? throw new InvalidOperationException("BTC network not available");

        var persister = _senderSessionStore.CreatePersister(session.SenderSessionId);
        using var replay = PayjoinMethods.ReplaySenderEventLog(persister);
        using var state = replay.State();

        switch (state)
        {
            case SendSession.WithReplyKey withReplyKey:
                await PostOriginalAsync(session, withReplyKey.Inner, persister, cancellationToken).ConfigureAwait(false);
                break;
            case SendSession.PollingForProposal polling:
                await PollForProposalAsync(session, polling.Inner, persister, network, cancellationToken).ConfigureAwait(false);
                break;
            case SendSession.SenderPendingFallback pendingFallback:
                await BroadcastFallbackAsync(session, pendingFallback.Inner, persister, network, cancellationToken).ConfigureAwait(false);
                break;
            case SendSession.Closed:
                // The library closed the session without this processor recording a broadcast:
                // nothing reached the network through us, so record the terminal state.
                _senderSessionStore.CompleteSession(
                    session.SenderSessionId,
                    PayjoinSenderSessionStatus.Failed,
                    broadcastTransactionId: null,
                    "the sender session closed without a broadcast");
                break;
        }
    }

    private async Task PostOriginalAsync(
        PayjoinSenderSessionState session,
        WithReplyKey sender,
        JsonSenderSessionPersister persister,
        CancellationToken cancellationToken)
    {
        var responseBody = await SendThroughRelayAsync(
            session,
            relay => sender.CreateV2PostRequest(relay),
            cancellationToken).ConfigureAwait(false);
        if (responseBody is null)
        {
            return;
        }

        using var transition = sender.ProcessResponse(responseBody.Value.Body, responseBody.Value.Context.OhttpCtx);
        using var polling = transition.Save(persister);
    }

    private async Task PollForProposalAsync(
        PayjoinSenderSessionState session,
        PollingForProposal polling,
        JsonSenderSessionPersister persister,
        BTCPayNetwork network,
        CancellationToken cancellationToken)
    {
        var responseBody = await SendThroughRelayAsync(
            session,
            relay => polling.CreatePollRequest(relay),
            cancellationToken).ConfigureAwait(false);
        if (responseBody is null)
        {
            return;
        }

        using var transition = polling.ProcessResponse(responseBody.Value.Body, responseBody.Value.Context.OhttpCtx);
        using var outcome = transition.Save(persister);
        if (outcome is not PollingForProposalTransitionOutcome.Progress progress)
        {
            return;
        }

        // The library already validated the proposal against the original during
        // ProcessResponse. The proposal arrives without HD keypaths (the receiver's
        // prepare_psbt strips them), so NBXplorer first restores the wallet metadata for
        // our inputs; SignAll then signs only inputs that match the store's derivation
        // scheme, and the receiver's contributed input is never touched here.
        var proposalPsbt = PSBT.Parse(progress.PsbtBase64, network.NBitcoinNetwork);
        var (derivationScheme, signer) = await ResolveSigningContextAsync(session.StoreId, network, cancellationToken).ConfigureAwait(false);
        var explorerClient = _explorerClientProvider.GetExplorerClient(network);
        var updateResponse = await explorerClient.UpdatePSBTAsync(
            new NBXplorer.Models.UpdatePSBTRequest
            {
                PSBT = proposalPsbt,
                DerivationScheme = derivationScheme.AccountDerivation
            },
            cancellationToken).ConfigureAwait(false);
        if (updateResponse?.PSBT is not null)
        {
            proposalPsbt = updateResponse.PSBT;
        }

        if (signer is null)
        {
            // The proposal is a different transaction from the original, so a wallet that
            // cannot sign on the server has to sign a second time. Park the session on a new
            // pending transaction and stop; the signature listener finishes the broadcast. If
            // the operator never signs, the library moves the session to its fallback state and
            // the original goes out instead, so the payment still completes.
            if (!RequestBaseUrl.TryFromUrl(session.RequestBaseUrl ?? string.Empty, out var requestBaseUrl))
            {
                throw new InvalidOperationException(
                    "the session recorded no base URL, so the second signing round cannot start");
            }

            var pending = await _pendingTransactionService.CreatePendingTransaction(
                session.StoreId,
                PayjoinConstants.BitcoinCode,
                proposalPsbt,
                requestBaseUrl,
                expiry: null,
                cancellationToken).ConfigureAwait(false);

            if (_senderSessionStore.AwaitSignature(session.SenderSessionId, pending.Id))
            {
                LogProposalAwaitingSignature(_logger, session.SenderSessionId, pending.Id, null);
            }

            return;
        }

        proposalPsbt = proposalPsbt.SignAll(derivationScheme.AccountDerivation, signer.AccountKey, signer.RootedKeyPath);
        if (!proposalPsbt.TryFinalize(out var errors))
        {
            throw new InvalidOperationException($"the payjoin proposal could not be finalized: {string.Join("; ", errors.Select(e => e.ToString()))}");
        }

        var payjoinTransaction = proposalPsbt.ExtractTransaction();
        await BroadcastAsync(network, payjoinTransaction, cancellationToken).ConfigureAwait(false);

        var payjoinTxId = payjoinTransaction.GetHash().ToString();
        LogSenderSessionBroadcast(_logger, session.SenderSessionId, payjoinTxId, null);
        _senderSessionStore.CompleteSession(
            session.SenderSessionId,
            PayjoinSenderSessionStatus.CompletedPayjoin,
            payjoinTxId,
            failureMessage: null);
    }

    private async Task BroadcastFallbackAsync(
        PayjoinSenderSessionState session,
        SenderPendingFallback pendingFallback,
        JsonSenderSessionPersister persister,
        BTCPayNetwork network,
        CancellationToken cancellationToken)
    {
        // The library moved the session here: the payjoin round is over (expiry or
        // cancellation), and the payment must still happen. Broadcast the original
        // transaction, then close the session through the library so the event log
        // records the handoff.
        var fallbackTransaction = Transaction.Load(pendingFallback.FallbackTx(), network.NBitcoinNetwork);
        await BroadcastAsync(network, fallbackTransaction, cancellationToken).ConfigureAwait(false);

        using var transition = pendingFallback.Close();
        transition.Save(persister);

        var fallbackTxId = fallbackTransaction.GetHash().ToString();
        LogSenderSessionBroadcast(_logger, session.SenderSessionId, fallbackTxId, null);
        _senderSessionStore.CompleteSession(
            session.SenderSessionId,
            PayjoinSenderSessionStatus.CompletedFallback,
            fallbackTxId,
            failureMessage: null);
    }

    private async Task<(byte[] Body, RequestOhttpContext Context)?> SendThroughRelayAsync(
        PayjoinSenderSessionState session,
        Func<string, RequestOhttpContext> buildRequest,
        CancellationToken cancellationToken)
    {
        var storeSettings = await _storeSettingsRepository.GetAsync(session.StoreId).ConfigureAwait(false);
        var relayUrls = storeSettings?.GetEffectiveOhttpRelayUrls() ?? [];
        System.Net.Http.HttpRequestException? lastError = null;
        foreach (var relayUrl in relayUrls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestContext = buildRequest(relayUrl.AbsoluteUri);
            try
            {
                var body = await _relayClient.SendAsync(
                    new SystemUri(requestContext.Request.Url),
                    requestContext.Request.ContentType,
                    requestContext.Request.Body,
                    cancellationToken).ConfigureAwait(false);
                return (body, requestContext);
            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                lastError = ex;
                requestContext.Dispose();
            }
        }

        LogSenderRelayUnavailable(_logger, session.SenderSessionId, lastError);
        return null;
    }

    /// <summary>
    /// Returns the derivation scheme, and the account key only when the server holds one. A null
    /// signer is the normal answer for a cold wallet, a hardware device or a multisig group, and
    /// it sends the proposal to BTCPay's pending transactions to be signed there.
    /// </summary>
    private async Task<(DerivationSchemeSettings DerivationScheme, SenderSigner? Signer)> ResolveSigningContextAsync(
        string storeId,
        BTCPayNetwork network,
        CancellationToken cancellationToken)
    {
        var store = await _storeRepository.FindStore(storeId).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"store {storeId} not found");
        var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(PayjoinConstants.BitcoinCode);
        var derivationScheme = store.GetPaymentMethodConfig<DerivationSchemeSettings>(paymentMethodId, _handlers, true)
            ?? throw new InvalidOperationException("derivation scheme not configured for BTC");
        if (!derivationScheme.IsHotWallet)
        {
            return (derivationScheme, null);
        }

        var explorerClient = _explorerClientProvider.GetExplorerClient(network);
        var signingKeyStr = await explorerClient.GetMetadataAsync<string>(
            derivationScheme.AccountDerivation,
            WellknownMetadataKeys.MasterHDKey,
            cancellationToken).ConfigureAwait(false);
        if (signingKeyStr is null)
        {
            return (derivationScheme, null);
        }

        var signingKey = ExtKey.Parse(signingKeyStr, network.NBitcoinNetwork);
        var rootedKeyPath = derivationScheme.GetAccountKeySettingsFromRoot(signingKey)?.GetRootedKeyPath();
        if (rootedKeyPath is null)
        {
            return (derivationScheme, null);
        }

        return (derivationScheme, new SenderSigner(signingKey.Derive(rootedKeyPath.KeyPath), rootedKeyPath));
    }

    private sealed record SenderSigner(ExtKey AccountKey, RootedKeyPath RootedKeyPath);

    private async Task BroadcastAsync(BTCPayNetwork network, Transaction transaction, CancellationToken cancellationToken)
    {
        var explorerClient = _explorerClientProvider.GetExplorerClient(network);
        var result = await explorerClient.BroadcastAsync(transaction, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException($"broadcast rejected: {result.RPCCodeMessage ?? result.RPCMessage ?? "unknown error"}");
        }
    }

    private void FailSession(string senderSessionId, string message, Exception exception)
    {
        LogSenderSessionFailed(_logger, senderSessionId, exception);
        _senderSessionStore.CompleteSession(
            senderSessionId,
            PayjoinSenderSessionStatus.Failed,
            broadcastTransactionId: null,
            message);
    }
}
