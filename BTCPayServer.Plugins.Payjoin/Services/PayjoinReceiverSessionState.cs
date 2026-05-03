using BTCPayServer.Client.Models;
using NBitcoin;
using System;
using System.Collections.Generic;
using System.Linq;
using SystemUri = System.Uri;

namespace BTCPayServer.Plugins.Payjoin.Services;

public sealed class PayjoinReceiverSessionState
{
    private readonly string[] _events;

    public PayjoinReceiverSessionState(
        string invoiceId,
        string storeId,
        string receiverAddress,
        SystemUri ohttpRelayUrl,
        DateTimeOffset monitoringExpiresAt,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        bool isCloseRequested = false,
        InvoiceStatus? closeInvoiceStatus = null,
        DateTimeOffset? closeRequestedAt = null,
        bool initializedPollAfterCloseRequestConsumed = false,
        string? contributedInputTransactionId = null,
        long? contributedInputOutputIndex = null,
        IEnumerable<string>? events = null)
    {
        InvoiceId = invoiceId;
        StoreId = storeId;
        ReceiverAddress = receiverAddress;
        OhttpRelayUrl = ohttpRelayUrl;
        MonitoringExpiresAt = monitoringExpiresAt;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        IsCloseRequested = isCloseRequested;
        CloseInvoiceStatus = closeInvoiceStatus;
        CloseRequestedAt = closeRequestedAt;
        InitializedPollAfterCloseRequestConsumed = initializedPollAfterCloseRequestConsumed;
        ContributedInputTransactionId = contributedInputTransactionId;
        ContributedInputOutputIndex = contributedInputOutputIndex;
        _events = events?.ToArray() ?? [];
    }

    public string InvoiceId { get; }

    public string StoreId { get; }

    public string ReceiverAddress { get; }

    public SystemUri OhttpRelayUrl { get; }

    public DateTimeOffset MonitoringExpiresAt { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; }

    public bool IsCloseRequested { get; }

    public InvoiceStatus? CloseInvoiceStatus { get; }

    public DateTimeOffset? CloseRequestedAt { get; }

    public bool InitializedPollAfterCloseRequestConsumed { get; }

    public string? ContributedInputTransactionId { get; }

    public long? ContributedInputOutputIndex { get; }

    internal bool CanPollInitializedAfterCloseRequest()
    {
        return IsCloseRequested && !InitializedPollAfterCloseRequestConsumed;
    }

    public bool TryGetContributedInput(out OutPoint outPoint)
    {
        outPoint = default!;

        if (string.IsNullOrWhiteSpace(ContributedInputTransactionId) || !ContributedInputOutputIndex.HasValue)
        {
            return false;
        }

        try
        {
            outPoint = new OutPoint(uint256.Parse(ContributedInputTransactionId), checked((uint)ContributedInputOutputIndex.Value));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    internal string[] GetEvents() => _events.ToArray();
}
