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

    Task ProcessPendingFallbackAsync(
        PayjoinReceiverStateContext context,
        ReceiverPendingFallback pendingFallback,
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

internal sealed record PayjoinOriginalTransactionFacts(
    IReadOnlyList<NBitcoin.OutPoint> InputOutpoints,
    IReadOnlyList<byte[]> OutputScripts);

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

    internal IReadOnlyList<NBitcoin.OutPoint> OriginalInputOutpoints { get; set; } = Array.Empty<NBitcoin.OutPoint>();

    internal IReadOnlyList<byte[]> OriginalOutputScripts { get; set; } = Array.Empty<byte[]>();

    internal PayjoinInputOwnershipResolver? InputOwnershipResolver { get; set; }

    internal PayjoinScriptOwnershipResolver? OutputOwnershipResolver { get; set; }
}
