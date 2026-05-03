using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

public sealed class PayjoinSessionBuildLock
{
    // This only serializes URI/session initialization inside this BTCPay process;
    // the database uniqueness constraints remain the cross-restart/process guard.
    private readonly Dictionary<string, SessionBuildLock> _sessionBuildLocks = new(StringComparer.Ordinal);
    private readonly object _sessionBuildLocksSync = new();

    public async Task<IDisposable> AcquireAsync(string invoiceId, CancellationToken cancellationToken)
    {
        SessionBuildLock sessionBuildLock;
        lock (_sessionBuildLocksSync)
        {
            if (!_sessionBuildLocks.TryGetValue(invoiceId, out sessionBuildLock!))
            {
                sessionBuildLock = new SessionBuildLock();
                _sessionBuildLocks.Add(invoiceId, sessionBuildLock);
            }

            sessionBuildLock.ReferenceCount++;
        }

        try
        {
            await sessionBuildLock.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new SessionBuildLockLease(this, invoiceId, sessionBuildLock);
        }
        catch
        {
            ReleaseSessionBuildLockReference(invoiceId, sessionBuildLock);
            throw;
        }
    }

    private void ReleaseSessionBuildLock(string invoiceId, SessionBuildLock sessionBuildLock)
    {
        sessionBuildLock.Semaphore.Release();
        ReleaseSessionBuildLockReference(invoiceId, sessionBuildLock);
    }

    private void ReleaseSessionBuildLockReference(string invoiceId, SessionBuildLock sessionBuildLock)
    {
        lock (_sessionBuildLocksSync)
        {
            sessionBuildLock.ReferenceCount--;
            if (sessionBuildLock.ReferenceCount == 0 &&
                _sessionBuildLocks.TryGetValue(invoiceId, out var current) &&
                ReferenceEquals(current, sessionBuildLock))
            {
                _sessionBuildLocks.Remove(invoiceId);
            }
        }
    }

    private sealed class SessionBuildLock
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int ReferenceCount { get; set; }
    }

    private sealed class SessionBuildLockLease : IDisposable
    {
        private readonly PayjoinSessionBuildLock _owner;
        private readonly string _invoiceId;
        private readonly SessionBuildLock _sessionBuildLock;
        private bool _disposed;

        public SessionBuildLockLease(PayjoinSessionBuildLock owner, string invoiceId, SessionBuildLock sessionBuildLock)
        {
            _owner = owner;
            _invoiceId = invoiceId;
            _sessionBuildLock = sessionBuildLock;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner.ReleaseSessionBuildLock(_invoiceId, _sessionBuildLock);
        }
    }
}
