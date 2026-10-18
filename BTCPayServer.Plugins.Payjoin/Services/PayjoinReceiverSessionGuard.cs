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
    private readonly ILogger<PayjoinReceiverSessionGuard> _logger;

    public PayjoinReceiverSessionGuard(
        PayjoinReceiverSessionStore sessionStore,
        BTCPayNetworkProvider networkProvider,
        InvoiceRepository invoiceRepository,
        ILogger<PayjoinReceiverSessionGuard> logger)
    {
        _sessionStore = sessionStore;
        _networkProvider = networkProvider;
        _invoiceRepository = invoiceRepository;
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

        var persister = _sessionStore.CreatePersister(session);
        ReplayResult? replay = null;
        ReceiveSession? state = null;
        try
        {
            if (TryExpireSession(session))
            {
                return null;
            }

            if (!TryGetReceiverScript(session, out var receiverScript))
            {
                LogPayjoinReceiverScriptUnavailable(_logger, session.InvoiceId, null);
                return null;
            }

            var replayScope = PayjoinMethods.ReplayReceiverEventLog(persister);
            replay = replayScope;

            var replayState = replayScope.State();
            state = replayState;

            // Resolve the original input outpoints before the synchronous ownership callback,
            // including when replay resumes mid-chain.
            byte[]? fallbackBytes;
            using (var history = replayScope.SessionHistory())
            {
                fallbackBytes = history.FallbackTx();
                using var uri = history.PjUri();
                if (fallbackBytes is { Length: > 0 } && uri.AmountSats() is not > 0)
                {
                    _sessionStore.FailPayment(persister, "original payload has no trustworthy session amount");
                    return null;
                }
            }

            if (TryRemoveCloseRequestedSession(session, replayState)) return null;

            _sessionStore.CompleteOwnershipLookupIfFinished(session, replayState);

            var result = new PayjoinReceiverSessionGuardResult(
                session,
                persister,
                receiverScript,
                replayScope,
                replayState,
                RemoveCloseRequestedSession);

            if (fallbackBytes is { Length: > 0 })
            {
                result.StateContext.OriginalTransaction = PayjoinReceiverStateProcessor.ExtractTransactionFacts(fallbackBytes);
            }

            replay = null;
            state = null;
            return result;
        }
        catch (Exception ex) when (PayjoinSessionStaleException.IsStale(ex))
        {
            (ex as IDisposable)?.Dispose();
            return null;
        }
        catch (ReceiverReplayException ex) when (ex.IsExpired())
        {
            ex.Dispose();
            RemoveSession(session.InvoiceId, "native receiver session expired", session.SessionInstanceId,
                persister.AttemptVersion);
            return null;
        }
        catch (ReceiverReplayException ex)
        {
            // Replay does not distinguish corrupt history from a temporary storage failure.
            // Neither is proof of a financial contradiction: preserve reconciliation eligibility.
            LogPayjoinReceiverReplayFailed(_logger, session.InvoiceId, ex.Message, ex);
            ex.Dispose();
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
            RemoveSession(session.InvoiceId, "invoice no longer exists", session.SessionInstanceId, session.AttemptVersion);
            return null;
        }

        if (invoice.GetInvoiceState().Status != InvoiceStatus.New)
        {
            var status = invoice.GetInvoiceState().Status;
            _sessionStore.RequestClose(session.InvoiceId, status, session.SessionInstanceId);
            return _sessionStore.GetAttempt(session.InvoiceId, session.SessionInstanceId);
        }

        return session;
    }

    internal bool TryExpireSession(PayjoinReceiverSessionState session)
    {
        var now = DateTimeOffset.UtcNow;
        var deadline = GetCleanupDeadline(session);
        if (now >= deadline)
        {
            RemoveSession(session.InvoiceId, $"cleanup deadline reached at {deadline:O}", session.SessionInstanceId, session.AttemptVersion);
            return true;
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
        RemoveSession(session.InvoiceId, $"invoice is no longer payable ({session.CloseInvoiceStatus})", session.SessionInstanceId, session.AttemptVersion);
        return true;
    }

    private bool RemoveSession(string invoiceId, string reason, Guid? sessionInstanceId, int? expectedVersion)
    {
        if (!_sessionStore.RemoveSession(invoiceId, sessionInstanceId, expectedVersion))
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
