using Payjoin;
using System;
using System.Threading;
using System.Threading.Tasks;
using SystemUri = System.Uri;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal interface IPayjoinReceiverStateProcessor
{
    Task ProcessInitializedAsync(
        PayjoinReceiverStateContext context,
        Initialized initialized,
        Func<WantsOutputs, PayjoinReceiverStateContext, CancellationToken, Task> continueWithOutputsAsync,
        CancellationToken cancellationToken);

    Task ProcessReplyableErrorAsync(
        PayjoinReceiverStateContext context,
        HasReplyableError replyableError,
        CancellationToken cancellationToken);

    Task ProcessUncheckedProposalAsync(
        PayjoinReceiverStateContext context,
        UncheckedOriginalPayload proposal,
        Func<WantsOutputs, PayjoinReceiverStateContext, CancellationToken, Task> continueWithOutputsAsync,
        CancellationToken cancellationToken);

    Task ProcessMaybeInputsOwnedAsync(
        PayjoinReceiverStateContext context,
        MaybeInputsOwned proposal,
        Func<WantsOutputs, PayjoinReceiverStateContext, CancellationToken, Task> continueWithOutputsAsync,
        CancellationToken cancellationToken);

    Task ProcessMaybeInputsSeenAsync(
        PayjoinReceiverStateContext context,
        MaybeInputsSeen proposal,
        Func<WantsOutputs, PayjoinReceiverStateContext, CancellationToken, Task> continueWithOutputsAsync,
        CancellationToken cancellationToken);

    Task ProcessOutputsUnknownAsync(
        PayjoinReceiverStateContext context,
        OutputsUnknown proposal,
        Func<WantsOutputs, PayjoinReceiverStateContext, CancellationToken, Task> continueWithOutputsAsync,
        CancellationToken cancellationToken);
}

internal sealed class PayjoinReceiverStateContext
{
    public PayjoinReceiverStateContext(
        PayjoinReceiverSessionState session,
        JsonReceiverSessionPersister persister,
        byte[] receiverScript,
        SystemUri ohttpRelayUrl,
        string storeId,
        string invoiceId,
        Func<PayjoinReceiverSessionState, bool> removeCloseRequestedSession)
    {
        Session = session;
        Persister = persister;
        ReceiverScript = receiverScript;
        OhttpRelayUrl = ohttpRelayUrl;
        StoreId = storeId;
        InvoiceId = invoiceId;
        RemoveCloseRequestedSession = removeCloseRequestedSession;
    }

    internal PayjoinReceiverSessionState Session { get; }

    internal JsonReceiverSessionPersister Persister { get; }

    internal byte[] ReceiverScript { get; }

    internal SystemUri OhttpRelayUrl { get; }

    internal string StoreId { get; }

    internal string InvoiceId { get; }

    internal Func<PayjoinReceiverSessionState, bool> RemoveCloseRequestedSession { get; }
}
