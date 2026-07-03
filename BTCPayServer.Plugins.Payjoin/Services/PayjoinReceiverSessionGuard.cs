using BTCPayServer.Client.Models;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Payjoin;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal sealed class PayjoinReceiverSessionGuard : IPayjoinReceiverSessionGuard
{
    // TODO: Replace these grace periods with an explicit receiver session lifetime policy.
    private static readonly TimeSpan CloseRequestedInitializedReplyGracePeriod = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CloseRequestedSettledInitializedReplyGracePeriod = TimeSpan.FromSeconds(20);

    private static readonly Action<ILogger, string, Exception?> LogPayjoinReceiverScriptUnavailable =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(3, nameof(LogPayjoinReceiverScriptUnavailable)),
            "Payjoin receiver script unavailable for {InvoiceId}");
    private static readonly Action<ILogger, string, string, Exception?> LogPayjoinReceiverReplayFailed =
        LoggerMessage.Define<string, string>(LogLevel.Warning, new EventId(5, nameof(LogPayjoinReceiverReplayFailed)),
            "Payjoin receiver replay failed for {InvoiceId}: {Message}");
    private static readonly Action<ILogger, string, string, Exception?> LogPayjoinReceiverSessionRemoved =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(9, nameof(LogPayjoinReceiverSessionRemoved)),
            "Payjoin receiver session removed for {InvoiceId}: {Reason}");

    private readonly PayjoinReceiverSessionStore _sessionStore;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly InvoiceRepository _invoiceRepository;
    private readonly IPayjoinFallbackBroadcaster _fallbackBroadcaster;
    private readonly ILogger<PayjoinReceiverSessionGuard> _logger;

    public PayjoinReceiverSessionGuard(
        PayjoinReceiverSessionStore sessionStore,
        BTCPayNetworkProvider networkProvider,
        InvoiceRepository invoiceRepository,
        IPayjoinFallbackBroadcaster fallbackBroadcaster,
        ILogger<PayjoinReceiverSessionGuard> logger)
    {
        _sessionStore = sessionStore;
        _networkProvider = networkProvider;
        _invoiceRepository = invoiceRepository;
        _fallbackBroadcaster = fallbackBroadcaster;
        _logger = logger;
    }

    public async Task<PayjoinReceiverSessionGuardResult?> TryPrepareAsync(
        PayjoinReceiverSessionState session,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var refreshedSession = await RefreshInvoiceCloseStateAsync(session).ConfigureAwait(false);
        if (refreshedSession is null)
        {
            return null;
        }

        session = refreshedSession;

        if (await TryExpireSessionAsync(session, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (!TryGetReceiverScript(session, out var receiverScript))
        {
            LogPayjoinReceiverScriptUnavailable(_logger, session.InvoiceId, null);
            return null;
        }

        var persister = _sessionStore.CreatePersister(session);
        ReplayResult? replay = null;
        ReceiveSession? state = null;
        try
        {
            var replayScope = PayjoinMethods.ReplayReceiverEventLog(persister);
            replay = replayScope;

            var replayState = replayScope.State();
            state = replayState;

            if (TryRemoveCloseRequestedSession(session, replayState))
            {
                return null;
            }

            // Give the ownership checks the original transaction's output scripts when the replayed
            // history already carries the sender's payload, so ownership is resolved before the
            // synchronous callbacks run even when a replay lands mid-chain.
            byte[]? fallbackBytes;
            using (var history = replayScope.SessionHistory())
            {
                fallbackBytes = history.FallbackTx();
            }

            var result = new PayjoinReceiverSessionGuardResult(
                session,
                persister,
                receiverScript,
                replayScope,
                replayState,
                RemoveCloseRequestedSession);

            if (fallbackBytes is { Length: > 0 })
            {
                result.StateContext.OriginalOutputScripts = PayjoinReceiverStateProcessor.ExtractOutputScripts(fallbackBytes);
            }

            replay = null;
            state = null;
            return result;
        }
        catch (ReceiverReplayException ex)
        {
            LogPayjoinReceiverReplayFailed(_logger, session.InvoiceId, ex.Message, ex);
            RemoveSession(session.InvoiceId, "receiver replay failed");
            return null;
        }
        finally
        {
            state?.Dispose();
            replay?.Dispose();
        }
    }

    internal async Task<PayjoinReceiverSessionState?> RefreshInvoiceCloseStateAsync(PayjoinReceiverSessionState session)
    {
        var invoice = await _invoiceRepository.GetInvoice(session.InvoiceId).ConfigureAwait(false);
        if (invoice is null)
        {
            RemoveSession(session.InvoiceId, "invoice no longer exists");
            return null;
        }

        if (invoice.GetInvoiceState().Status != InvoiceStatus.New)
        {
            var status = invoice.GetInvoiceState().Status;
            _sessionStore.RequestClose(session.InvoiceId, status);
            return _sessionStore.TryGetSession(session.InvoiceId, out var closeRequestedSession)
                ? closeRequestedSession
                : null;
        }

        return session;
    }

    internal async Task<bool> TryExpireSessionAsync(PayjoinReceiverSessionState session, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var deadline = GetCleanupDeadline(session);
        if (now >= deadline)
        {
            // BIP 78 safety net: the session held the sender's signed original as the fallback; if the
            // payjoin never completed and the invoice is still unpaid, broadcast it before the session
            // (and its event log, which holds the raw transaction) is deleted.
            await _fallbackBroadcaster.TryBroadcastFallbackSafetyNetAsync(session, cancellationToken).ConfigureAwait(false);
            return RemoveSession(session.InvoiceId, $"cleanup deadline reached at {deadline:O}");
        }

        return false;
    }

    internal bool TryRemoveCloseRequestedSession(PayjoinReceiverSessionState session, ReceiveSession state)
    {
        if (!session.IsCloseRequested)
        {
            return false;
        }

        if (StateCanStillReplyAfterCloseRequest(session, state))
        {
            return false;
        }

        return RemoveCloseRequestedSession(session);
    }

    private bool RemoveCloseRequestedSession(PayjoinReceiverSessionState session)
    {
        return RemoveSession(session.InvoiceId, $"invoice is no longer payable ({session.CloseInvoiceStatus})");
    }

    private bool RemoveSession(string invoiceId, string reason)
    {
        if (!_sessionStore.RemoveSession(invoiceId))
        {
            return false;
        }

        LogPayjoinReceiverSessionRemoved(_logger, invoiceId, reason, null);
        return true;
    }

    private static bool StateCanStillReplyAfterCloseRequest(PayjoinReceiverSessionState session, ReceiveSession state)
    {
        if (state is ReceiveSession.UncheckedOriginalPayload or ReceiveSession.HasReplyableError)
        {
            return true;
        }

        if (state is not ReceiveSession.Initialized)
        {
            return false;
        }

        if (!session.InitializedPollAfterCloseRequestConsumed)
        {
            return true;
        }

        var gracePeriod = session.CloseInvoiceStatus is InvoiceStatus.Processing or InvoiceStatus.Settled
            ? CloseRequestedSettledInitializedReplyGracePeriod
            : CloseRequestedInitializedReplyGracePeriod;

        return session.CloseRequestedAt is { } closeRequestedAt &&
               DateTimeOffset.UtcNow - closeRequestedAt < gracePeriod;
    }

    private static DateTimeOffset GetCleanupDeadline(PayjoinReceiverSessionState session)
    {
        return session.MonitoringExpiresAt;
    }

    private bool TryGetReceiverScript(PayjoinReceiverSessionState session, out byte[] script)
    {
        script = Array.Empty<byte>();
        var network = _networkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode)?.NBitcoinNetwork;
        if (network is null)
        {
            return false;
        }

        try
        {
            var address = BitcoinAddress.Create(session.ReceiverAddress, network);
            script = address.ScriptPubKey.ToBytes();
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
