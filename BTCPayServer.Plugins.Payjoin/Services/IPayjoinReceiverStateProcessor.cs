using Payjoin;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

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
        string storeId,
        string invoiceId,
        Func<PayjoinReceiverSessionState, bool> removeCloseRequestedSession)
    {
        Session = session;
        Persister = persister;
        ReceiverScript = receiverScript;
        StoreId = storeId;
        InvoiceId = invoiceId;
        RemoveCloseRequestedSession = removeCloseRequestedSession;
    }

    internal PayjoinReceiverSessionState Session { get; }

    internal JsonReceiverSessionPersister Persister { get; }

    internal byte[] ReceiverScript { get; }

    internal string StoreId { get; }

    internal string InvoiceId { get; }

    internal Func<PayjoinReceiverSessionState, bool> RemoveCloseRequestedSession { get; }

    /// <summary>
    /// Output scripts of the sender's original transaction, extracted from the session replay when
    /// available. Used to resolve output ownership ahead of the synchronous rust-payjoin callbacks.
    /// </summary>
    internal IReadOnlyList<byte[]> OriginalOutputScripts { get; set; } = Array.Empty<byte[]>();

    /// <summary>
    /// Ownership data resolved once per processing tick and shared by the ownership checks in the
    /// same chain, so the wallet is not queried again between the inputs and outputs checks.
    /// </summary>
    internal PayjoinScriptOwnershipResolver? OwnershipResolver { get; set; }
}
