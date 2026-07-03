using Payjoin;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.Payjoin.Services;

/// <summary>
/// Collects the events a rust-payjoin transition wants to persist instead of writing them
/// immediately, so a caller can store them together with related state in one database
/// transaction.
/// </summary>
internal sealed class CapturingReceiverSessionPersister : JsonReceiverSessionPersister
{
    private readonly List<string> _events = [];

    public IReadOnlyList<string> Events => _events;

    public void Save(string @event)
    {
        _events.Add(@event);
    }

    public string[] Load() => _events.ToArray();

    public void Close()
    {
    }
}
