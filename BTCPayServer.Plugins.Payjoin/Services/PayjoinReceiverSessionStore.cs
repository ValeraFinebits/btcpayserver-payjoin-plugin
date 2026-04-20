using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using NBitcoin;
using Payjoin;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using SystemUri = System.Uri;

namespace BTCPayServer.Plugins.Payjoin.Services;

// TODO: Persist receiver sessions to the database so that active payjoin negotiations survive server restarts.
public sealed class PayjoinReceiverSessionStore
{
    private readonly ConcurrentDictionary<string, PayjoinReceiverSessionState> _sessions = new();

    public PayjoinReceiverSessionStore(ApplicationDbContextFactory? dbContextFactory = null)
    {
    }

    public PayjoinReceiverSessionState CreateSession(
        string invoiceId,
        string receiverAddress,
        string storeId,
        SystemUri ohttpRelayUrl,
        DateTimeOffset monitoringExpiresAt,
        out bool created)
    {
        PayjoinReceiverSessionState? createdSession = null;
        var session = _sessions.GetOrAdd(invoiceId, _ =>
        {
            createdSession = new PayjoinReceiverSessionState(
                invoiceId,
                storeId,
                receiverAddress,
                ohttpRelayUrl,
                monitoringExpiresAt,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
            return createdSession;
        });
        created = ReferenceEquals(session, createdSession);
        return session;
    }

    public bool TryGetSession(string invoiceId, out PayjoinReceiverSessionState? session)
    {
        return _sessions.TryGetValue(invoiceId, out session);
    }

    public IReadOnlyCollection<PayjoinReceiverSessionState> GetSessions()
    {
        return _sessions.Values.ToArray();
    }

    public bool RemoveSession(string invoiceId)
    {
        return _sessions.TryRemove(invoiceId, out _);
    }

    public bool RequestClose(string invoiceId, InvoiceStatus invoiceStatus)
    {
        return _sessions.TryGetValue(invoiceId, out var session) && session.RequestClose(invoiceStatus);
    }

    internal static JsonReceiverSessionPersister CreatePersister(PayjoinReceiverSessionState session)
    {
        return new InMemoryReceiverPersister(session);
    }

    private sealed class InMemoryReceiverPersister : JsonReceiverSessionPersister
    {
        private readonly PayjoinReceiverSessionState _session;

        public InMemoryReceiverPersister(PayjoinReceiverSessionState session)
        {
            _session = session;
        }

        public void Save(string @event)
        {
            _session.AddEvent(@event);
        }

        public string[] Load() => _session.GetEvents();

        public void Close()
        {
        }
    }
}

public sealed class PayjoinReceiverSessionState
{
    private readonly ConcurrentQueue<string> _events = new();
    // TODO: Move this receiver signing context into a dedicated component if payjoin proposal signing is extracted from the poller.
    // TODO: Replace this BTCPay-specific persisted metadata with selected-input data from rust-payjoin/payjoin-ffi if that becomes available.
    private PayjoinReceiverContributedInput[] _contributedInputs = Array.Empty<PayjoinReceiverContributedInput>();
    private bool _finalInitializedPollAfterCloseRequestAvailable;

    public PayjoinReceiverSessionState(
        string invoiceId,
        string storeId,
        string receiverAddress,
        SystemUri ohttpRelayUrl,
        DateTimeOffset monitoringExpiresAt,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        InvoiceId = invoiceId;
        StoreId = storeId;
        ReceiverAddress = receiverAddress;
        OhttpRelayUrl = ohttpRelayUrl;
        MonitoringExpiresAt = monitoringExpiresAt;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public string InvoiceId { get; }
    public string StoreId { get; }
    public string ReceiverAddress { get; }
    public SystemUri OhttpRelayUrl { get; }
    public DateTimeOffset MonitoringExpiresAt { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public bool IsCloseRequested { get; private set; }
    public InvoiceStatus? CloseInvoiceStatus { get; private set; }

    internal void AddEvent(string @event)
    {
        _events.Enqueue(@event);
        Touch();
    }

    internal string[] GetEvents()
    {
        return _events.ToArray();
    }

    internal void SetContributedInputs(params PayjoinReceiverContributedInput[] contributedInputs)
    {
        _contributedInputs = contributedInputs.ToArray();
        Touch();
    }

    internal PayjoinReceiverContributedInput[] GetContributedInputs()
    {
        return _contributedInputs.ToArray();
    }

    internal void ClearContributedInputs()
    {
        if (_contributedInputs.Length == 0)
        {
            return;
        }

        _contributedInputs = Array.Empty<PayjoinReceiverContributedInput>();
        Touch();
    }

    internal bool RequestClose(InvoiceStatus invoiceStatus)
    {
        var changed = !IsCloseRequested || CloseInvoiceStatus != invoiceStatus;
        IsCloseRequested = true;
        CloseInvoiceStatus = invoiceStatus;
        if (changed)
        {
            _finalInitializedPollAfterCloseRequestAvailable = true;
            Touch();
        }

        return changed;
    }

    internal bool CanPollInitializedAfterCloseRequest()
    {
        return IsCloseRequested && _finalInitializedPollAfterCloseRequestAvailable;
    }

    internal bool TryConsumeInitializedPollAfterCloseRequest()
    {
        if (!CanPollInitializedAfterCloseRequest())
        {
            return false;
        }

        _finalInitializedPollAfterCloseRequestAvailable = false;
        Touch();
        return true;
    }

    internal void Touch()
    {
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}

// TODO: Rename or replace this type with a more explicit receiver signing context model if it grows beyond persisted input identity metadata.
internal sealed record PayjoinReceiverContributedInput(OutPoint OutPoint, KeyPath KeyPath);
