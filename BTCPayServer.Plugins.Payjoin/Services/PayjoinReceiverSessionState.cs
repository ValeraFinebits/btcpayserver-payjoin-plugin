using BTCPayServer.Client.Models;
using NBitcoin;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace BTCPayServer.Plugins.Payjoin.Services;

public sealed class PayjoinReceiverSessionState
{
    private readonly string[] _events;

    [SuppressMessage("Design", "CA1054:URI-like parameters should not be strings", Justification = "A bitcoin: BIP21 URI is stored and merged as text; System.Uri would re-encode its query parameters.")]
    public PayjoinReceiverSessionState(
        string invoiceId,
        string storeId,
        string receiverAddress,
        DateTimeOffset monitoringExpiresAt,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        bool isCloseRequested = false,
        InvoiceStatus? closeInvoiceStatus = null,
        DateTimeOffset? closeRequestedAt = null,
        bool initializedPollAfterCloseRequestConsumed = false,
        string? contributedInputTransactionId = null,
        long? contributedInputOutputIndex = null,
        IEnumerable<string>? events = null,
        string? payjoinUri = null)
    {
        InvoiceId = invoiceId;
        StoreId = storeId;
        ReceiverAddress = receiverAddress;
        MonitoringExpiresAt = monitoringExpiresAt;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        IsCloseRequested = isCloseRequested;
        CloseInvoiceStatus = closeInvoiceStatus;
        CloseRequestedAt = closeRequestedAt;
        InitializedPollAfterCloseRequestConsumed = initializedPollAfterCloseRequestConsumed;
        ContributedInputTransactionId = contributedInputTransactionId;
        ContributedInputOutputIndex = contributedInputOutputIndex;
        PayjoinUri = payjoinUri;
        _events = events?.ToArray() ?? [];
    }

    [SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "A bitcoin: BIP21 URI is stored and merged as text; System.Uri would re-encode its query parameters.")]
    public string? PayjoinUri { get; }

    public string InvoiceId { get; }

    public string StoreId { get; }

    public string ReceiverAddress { get; }

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

    internal PayjoinSessionServability GetServability() => new(
        _events.Length > 0,
        IsCloseRequested,
        MonitoringExpiresAt,
        ReceiverAddress);
}
