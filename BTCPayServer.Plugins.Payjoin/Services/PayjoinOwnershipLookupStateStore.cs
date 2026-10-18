using NBitcoin;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal enum PayjoinOwnershipLookupStatus { Ready, Deferred, Exhausted, CapacityUnavailable }

internal sealed record PayjoinOwnershipLookupResult
{
    private PayjoinOwnershipLookupResult(PayjoinOwnershipLookupStatus status, PayjoinInputOwnershipResolver? resolver = null)
    {
        Status = status;
        Resolver = resolver;
    }

    internal PayjoinOwnershipLookupStatus Status { get; }
    internal PayjoinInputOwnershipResolver? Resolver { get; }
    internal static PayjoinOwnershipLookupResult Ready(PayjoinInputOwnershipResolver resolver) => new(PayjoinOwnershipLookupStatus.Ready, resolver);
    internal static PayjoinOwnershipLookupResult Unavailable(PayjoinOwnershipLookupStatus status)
        => status == PayjoinOwnershipLookupStatus.Ready
            ? throw new ArgumentException("A ready lookup requires a resolver.", nameof(status))
            : new(status);
}

internal readonly record struct PayjoinOwnershipSessionKey(string StoreId, string InvoiceId, Guid SessionInstanceId)
{
    internal static PayjoinOwnershipSessionKey From(PayjoinReceiverSessionState session)
        => new(session.StoreId, session.InvoiceId, session.SessionInstanceId);
}

internal sealed record PayjoinOwnershipProposalKey(
    PayjoinOwnershipSessionKey Session, string? PayjoinUri, uint256 TransactionId,
    string HotDerivation);

internal sealed class PayjoinOwnershipLookupStateStore
{
    internal const int MaxPasses = 3;
    internal const int MaxConcurrentPasses = 4;
    internal const int MaxCachedProposals = 1024;
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    private readonly object _sync = new();
    private readonly TimeProvider _clock;
    private readonly Dictionary<PayjoinOwnershipProposalKey, ProposalLookupState> _proposals = new();
    private readonly HashSet<LookupPass> _calls = new();
    private int _runningPasses;
    private bool _capacityReported;

    public PayjoinOwnershipLookupStateStore() : this(TimeProvider.System) { }
    internal PayjoinOwnershipLookupStateStore(TimeProvider clock) => _clock = clock;

    internal PayjoinOwnershipLookupStatus TryEnter(PayjoinReceiverSessionState session, out LookupPass? pass)
    {
        lock (_sync)
        {
            pass = null;
            PruneExpired();
            if (session.MonitoringExpiresAt <= _clock.GetUtcNow()) return PayjoinOwnershipLookupStatus.Exhausted;
            if (_calls.Count >= MaxCachedProposals) return PayjoinOwnershipLookupStatus.CapacityUnavailable;
            pass = new LookupPass(this, PayjoinOwnershipSessionKey.From(session), session.MonitoringExpiresAt);
            _calls.Add(pass);
            return PayjoinOwnershipLookupStatus.Ready;
        }
    }

    internal PayjoinOwnershipLookupStatus TryBegin(LookupPass pass, PayjoinOwnershipProposalKey key,
        out ProposalLookupState? state, out bool newlyExhausted)
    {
        lock (_sync)
        {
            state = null;
            newlyExhausted = false;
            PruneExpired();
            if (IsRetiredCore(pass)) return PayjoinOwnershipLookupStatus.Exhausted;
            _proposals.TryGetValue(key, out var existing);
            if (existing is not null)
            {
                if (existing.Running) return PayjoinOwnershipLookupStatus.Deferred;
                if (existing.Exhausted) return PayjoinOwnershipLookupStatus.Exhausted;
                if (existing.PassesStarted >= MaxPasses)
                {
                    existing.Exhausted = true;
                    newlyExhausted = true;
                    return PayjoinOwnershipLookupStatus.Exhausted;
                }
                if (existing.RetryAfter > _clock.GetUtcNow()) return PayjoinOwnershipLookupStatus.Deferred;
            }
            if (_runningPasses >= MaxConcurrentPasses) return PayjoinOwnershipLookupStatus.Deferred;
            if (existing is null)
            {
                if (_proposals.Count >= MaxCachedProposals) return PayjoinOwnershipLookupStatus.CapacityUnavailable;
                existing = new ProposalLookupState(pass.ExpiresAt);
                _proposals.Add(key, existing);
            }
            existing.Running = true;
            existing.PassesStarted++;
            _runningPasses++;
            pass.Proposal = existing;
            state = existing;
            return PayjoinOwnershipLookupStatus.Ready;
        }
    }

    internal PayjoinOwnershipLookupStatus Fail(LookupPass pass, bool budgetExceeded)
    {
        lock (_sync)
        {
            if (IsRetiredCore(pass)) return PayjoinOwnershipLookupStatus.Exhausted;
            var state = pass.Proposal!;
            if (budgetExceeded || state.PassesStarted >= MaxPasses)
            {
                state.Exhausted = true;
                return PayjoinOwnershipLookupStatus.Exhausted;
            }
            state.RetryAfter = _clock.GetUtcNow() + RetryDelay;
            return PayjoinOwnershipLookupStatus.Deferred;
        }
    }

    internal bool IsRetired(LookupPass pass)
    {
        lock (_sync) return IsRetiredCore(pass);
    }

    internal bool ShouldReportCapacity()
    {
        lock (_sync)
        {
            if (_capacityReported) return false;
            _capacityReported = true;
            return true;
        }
    }

    internal void Forget(PayjoinOwnershipSessionKey session)
    {
        lock (_sync)
        {
            foreach (var call in _calls.Where(call => call.Session == session)) call.Retired = true;
            foreach (var key in _proposals.Keys.Where(key => key.Session == session).ToArray()) _proposals.Remove(key);
            if (_proposals.Count < MaxCachedProposals && _calls.Count < MaxCachedProposals) _capacityReported = false;
        }
    }

    private bool IsRetiredCore(LookupPass pass) => pass.Retired || pass.ExpiresAt <= _clock.GetUtcNow();

    private void PruneExpired()
    {
        var now = _clock.GetUtcNow();
        foreach (var key in _proposals.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray()) _proposals.Remove(key);
        foreach (var call in _calls.Where(call => call.ExpiresAt <= now)) call.Retired = true;
        if (_proposals.Count < MaxCachedProposals && _calls.Count < MaxCachedProposals) _capacityReported = false;
    }

    private void End(LookupPass pass)
    {
        lock (_sync)
        {
            if (!_calls.Remove(pass)) return;
            if (pass.Proposal is not null)
            {
                pass.Proposal.Running = false;
                _runningPasses--;
            }
            if (_proposals.Count < MaxCachedProposals && _calls.Count < MaxCachedProposals) _capacityReported = false;
        }
    }

    internal sealed class LookupPass : IDisposable
    {
        private readonly PayjoinOwnershipLookupStateStore _owner;
        internal LookupPass(PayjoinOwnershipLookupStateStore owner, PayjoinOwnershipSessionKey session, DateTimeOffset expiresAt)
        {
            _owner = owner;
            Session = session;
            ExpiresAt = expiresAt;
        }
        internal PayjoinOwnershipSessionKey Session { get; }
        internal DateTimeOffset ExpiresAt { get; }
        internal bool Retired { get; set; }
        internal ProposalLookupState? Proposal { get; set; }
        internal int RequestCount { get; set; }
        public void Dispose() => _owner.End(this);
    }

    internal sealed class ProposalLookupState(DateTimeOffset expiresAt)
    {
        internal DateTimeOffset ExpiresAt { get; } = expiresAt;
        internal bool Running { get; set; }
        internal bool Exhausted { get; set; }
        internal int PassesStarted { get; set; }
        internal DateTimeOffset RetryAfter { get; set; }
        internal HashSet<OutPoint> OwnedInputs { get; } = new();
        internal HashSet<Script> OwnedScripts { get; } = new();
    }
}
