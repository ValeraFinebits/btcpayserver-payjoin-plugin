using Payjoin;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal interface IPayjoinReceiverSessionGuard
{
    Task<PayjoinReceiverSessionGuardResult?> TryPrepareAsync(
        PayjoinReceiverSessionState session,
        CancellationToken cancellationToken);
}

internal sealed class PayjoinReceiverSessionGuardResult : IDisposable
{
    private readonly ReplayResult _replay;
    private readonly ReceiveSession _state;

    public PayjoinReceiverSessionGuardResult(
        PayjoinReceiverSessionState session,
        JsonReceiverSessionPersister persister,
        byte[] receiverScript,
        ReplayResult replay,
        ReceiveSession state,
        Func<PayjoinReceiverSessionState, bool> removeCloseRequestedSession)
    {
        Session = session;
        Persister = persister;
        ReceiverScript = receiverScript;
        _replay = replay;
        _state = state;
        StateContext = new PayjoinReceiverStateContext(
            session,
            persister,
            receiverScript,
            session.OhttpRelayUrl!,
            session.StoreId,
            session.InvoiceId,
            removeCloseRequestedSession);
    }

    internal PayjoinReceiverSessionState Session { get; }

    internal JsonReceiverSessionPersister Persister { get; }

    internal byte[] ReceiverScript { get; }

    internal ReceiveSession State => _state;

    internal PayjoinReceiverStateContext StateContext { get; }

    public void Dispose()
    {
        _state.Dispose();
        _replay.Dispose();
    }
}
