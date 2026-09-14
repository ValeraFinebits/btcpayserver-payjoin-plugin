using Payjoin;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.Payjoin.Services;

/// <summary>
/// Buffers sender session events during bootstrap, before the durable session row exists, so the
/// captured log can seed the database-backed persister in one write.
/// </summary>
internal sealed class CapturingSenderSessionPersister : JsonSenderSessionPersister
{
    private readonly List<string> _events = [];

    public void Save(string @event)
    {
        _events.Add(@event);
    }

    public string[] Load() => [.. _events];

    public void Close()
    {
    }
}
