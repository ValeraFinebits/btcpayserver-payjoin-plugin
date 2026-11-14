using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.RPC;
using NBXplorer.Models;
using Payjoin;
using Xunit;
using InvoiceStatus = BTCPayServer.Client.Models.InvoiceStatus;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public partial class PayjoinReceiverRelayFlowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnershipBudgetCancellationIsPersistedBeforeBroadcastAndCleanup(bool failSave)
    {
        using var lookups = new PayjoinOwnershipLookupBudgetTests.LookupFixture();
        using var fixture = new ReceiverFixture(ownershipLookups: lookups.States);
        await PrepareBudgetCancellationAsync(fixture);
        var session = fixture.Session();
        var overBudget = lookups.CreateOriginal(PayjoinWalletOwnershipService.MaxRequestsPerPass / 2);
        Assert.Equal(PayjoinOwnershipLookupStatus.Exhausted, (await lookups.LookupAsync(overBudget, session)).Status);
        var requests = lookups.Handler.Calls.Count;
        var bridge = await fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None);
        var original = OriginalFallback(fixture);
        fixture.Fallback.RespondAsync = (_, _) =>
        {
            AssertReceiverState<ReceiveSession.ReceiverPendingFallback>(fixture);
            return Task.FromResult<BroadcastResult?>(new BroadcastResult(true));
        };
        fixture.Factory.FailSaveChanges = failSave;

        await fixture.Processor.ProcessTickAsync(CancellationToken.None);

        fixture.Factory.FailSaveChanges = false;
        if (failSave)
        {
            AssertReceiverState<ReceiveSession.MaybeInputsOwned>(fixture);
            Assert.Empty(fixture.Fallback.Calls);
            Assert.Equal(PayjoinOwnershipLookupStatus.Exhausted, (await lookups.LookupAsync(overBudget, session)).Status);
            Assert.Equal(requests, lookups.Handler.Calls.Count);
        }
        else
        {
            AssertReceiverState<ReceiveSession.Closed>(fixture);
            Assert.Equal(original, Assert.Single(fixture.Fallback.Calls));
            Assert.Equal(PayjoinOwnershipLookupStatus.Exhausted, (await lookups.LookupAsync(overBudget, session)).Status);
            Assert.Equal(2 * requests, lookups.Handler.Calls.Count);
        }
        Assert.Equal(bridge, await fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None));
        using var db = fixture.Factory.CreateContext();
        Assert.Empty(db.ReceiverSeenInputs);
        Assert.Empty(db.ReceiverInputReservations);
        Assert.Equal(0, fixture.Inputs.ContributionCalls);
        Assert.Equal(0, fixture.ProposalSigner.Calls);
    }

    [Theory]
    [InlineData("Deferred")]
    [InlineData("CapacityUnavailable")]
    [InlineData("Retired")]
    public async Task UnavailableOwnershipDoesNotCancelOrPublish(string status)
    {
        using var fixture = new ReceiverFixture();
        await PrepareBudgetCancellationAsync(fixture);
        var events = fixture.Events();
        fixture.Wallet.Status = Enum.Parse<PayjoinOwnershipLookupStatus>(status);
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        Assert.Equal(events, fixture.Events());
        AssertReceiverState<ReceiveSession.MaybeInputsOwned>(fixture);
        Assert.Empty(fixture.Fallback.Calls);
        Assert.Equal(0, fixture.Inputs.ContributionCalls);
        Assert.Equal(0, fixture.ProposalSigner.Calls);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("known")]
    [InlineData("invalid-transaction")]
    public async Task DefinitiveFallbackResultsCloseWithoutConfirmingPayment(string outcome)
    {
        using var fixture = new ReceiverFixture();
        await PrepareBudgetCancellationAsync(fixture);
        var bridge = await fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None);
        fixture.Fallback.RespondAsync = (_, _) => Task.FromResult<BroadcastResult?>(outcome switch
        {
            "success" => new BroadcastResult(true),
            "known" => new BroadcastResult(false) { RPCCode = RPCErrorCode.RPC_TRANSACTION_ALREADY_IN_CHAIN },
            "invalid-transaction" => new BroadcastResult(false) { RPCCode = RPCErrorCode.RPC_DESERIALIZATION_ERROR },
            _ => new BroadcastResult(false) { RPCCode = RPCErrorCode.RPC_TRANSACTION_REJECTED }
        });

        await fixture.Processor.ProcessTickAsync(CancellationToken.None);

        AssertReceiverState<ReceiveSession.Closed>(fixture);
        Assert.Single(fixture.Fallback.Calls);
        Assert.Equal(bridge, await fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None));
        Assert.Equal(outcome is "success" or "known" ? 0 : 1, fixture.Fallback.Log.Events.Count(id => id == 1));
    }

    [Theory]
    [InlineData("http")]
    [InlineData("rpc")]
    [InlineData("missing-inputs")]
    [InlineData("rejected")]
    [InlineData("warmup")]
    [InlineData("unknown-code")]
    [InlineData("ambiguous")]
    [InlineData("contradictory")]
    [InlineData("null")]
    [InlineData("timeout")]
    [InlineData("shutdown")]
    public async Task RecoverableFallbackResultsRemainPendingAcrossServiceRestart(string outcome)
    {
        using var fixture = new ReceiverFixture(fallbackBroadcastTimeout: TimeSpan.FromMilliseconds(100));
        await PrepareBudgetCancellationAsync(fixture);
        var bridge = await fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        fixture.Fallback.RespondAsync = async (_, token) =>
        {
            if (outcome == "http") throw new HttpRequestException("Lost broadcast response.");
            if (outcome == "rpc") throw RpcFailure(RPCErrorCode.RPC_IN_WARMUP);
            if (outcome is "timeout" or "shutdown")
            {
                if (outcome == "shutdown") shutdown.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            }
            return outcome switch
            {
                "warmup" => new BroadcastResult(false) { RPCCode = RPCErrorCode.RPC_IN_WARMUP },
                "missing-inputs" => new BroadcastResult(false) { RPCCode = RPCErrorCode.RPC_TRANSACTION_ERROR },
                "rejected" => new BroadcastResult(false) { RPCCode = RPCErrorCode.RPC_TRANSACTION_REJECTED },
                "unknown-code" => new BroadcastResult(false) { RPCCode = (RPCErrorCode)(-999) },
                "contradictory" => new BroadcastResult(true) { RPCCode = RPCErrorCode.RPC_IN_WARMUP },
                "null" => null,
                _ => new BroadcastResult(false)
            };
        };
        if (outcome == "shutdown")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Processor.ProcessTickAsync(shutdown.Token));
        else
            await fixture.Processor.ProcessTickAsync(TestContext.Current.CancellationToken);

        AssertReceiverState<ReceiveSession.ReceiverPendingFallback>(fixture);
        var lookups = fixture.Wallet.InputLookupCalls;
        fixture.Fallback.RespondAsync = (_, _) => Task.FromResult<BroadcastResult?>(new BroadcastResult(false)
        {
            RPCCode = RPCErrorCode.RPC_TRANSACTION_ALREADY_IN_CHAIN
        });
        await fixture.RecreateProcessor().ProcessTickAsync(TestContext.Current.CancellationToken);

        AssertReceiverState<ReceiveSession.Closed>(fixture);
        Assert.Equal(2, fixture.Fallback.Calls.Count);
        Assert.Equal(fixture.Fallback.Calls[0], fixture.Fallback.Calls[1]);
        Assert.Equal(lookups, fixture.Wallet.InputLookupCalls);
        Assert.Equal(bridge, await fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None));
        Assert.Equal(0, fixture.Inputs.ContributionCalls);
        Assert.Equal(0, fixture.ProposalSigner.Calls);
    }

    [Fact]
    public async Task FailedCloseCanRepeatTheSameAlreadyPublishedOriginal()
    {
        using var fixture = new ReceiverFixture();
        await PrepareBudgetCancellationAsync(fixture);
        fixture.Fallback.RespondAsync = (_, _) =>
        {
            fixture.Factory.FailSaveChanges = true;
            return Task.FromResult<BroadcastResult?>(new BroadcastResult(true));
        };
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        fixture.Factory.FailSaveChanges = false;
        AssertReceiverState<ReceiveSession.ReceiverPendingFallback>(fixture);
        var requests = fixture.Wallet.InputLookupCalls;
        fixture.Fallback.RespondAsync = (_, _) => Task.FromResult<BroadcastResult?>(new BroadcastResult(true));
        for (var tick = 0; tick < 3; tick++) await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        Assert.Single(fixture.Fallback.Calls);
        fixture.Fallback.Clock.Advance(PayjoinReceiverStateProcessor.FallbackRetryDelay);

        await fixture.Processor.ProcessTickAsync(CancellationToken.None);

        AssertReceiverState<ReceiveSession.Closed>(fixture);
        Assert.Equal(2, fixture.Fallback.Calls.Count);
        Assert.Equal(fixture.Fallback.Calls[0], fixture.Fallback.Calls[1]);
        Assert.Equal(requests, fixture.Wallet.InputLookupCalls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StaleAttemptCannotCancelOrCloseAfterReplacement(bool duringBroadcast, bool replaceGeneration)
    {
        using var fixture = new ReceiverFixture();
        await PrepareBudgetCancellationAsync(fixture);
        string[]? winningEvents = null;
        PayjoinReceiverSessionState? winner = null;
        void ReplaceAttempt()
        {
            if (replaceGeneration)
            {
                var address = fixture.Session().ReceiverAddress;
                var events = fixture.Events();
                Assert.True(fixture.Store.RemoveSession("invoice"));
                fixture.Store.GetOrCreateSession("invoice", address, "store", DateTimeOffset.UtcNow.AddMinutes(20), events);
            }
            else
            {
                using var db = fixture.Factory.CreateContext();
                db.ReceiverSessions.Single().AttemptVersion++;
                db.SaveChanges();
            }
            winner = fixture.Session();
            winningEvents = fixture.Events();
        }
        if (duringBroadcast)
            fixture.Fallback.RespondAsync = (_, _) => { ReplaceAttempt(); return Task.FromResult<BroadcastResult?>(new BroadcastResult(true)); };
        else
            fixture.Wallet.BeforeInputLookup = () => { ReplaceAttempt(); return Task.CompletedTask; };

        await fixture.Processor.ProcessTickAsync(CancellationToken.None);

        Assert.NotNull(winner);
        Assert.Equal(winner.SessionInstanceId, fixture.Session().SessionInstanceId);
        Assert.Equal(winner.AttemptVersion, fixture.Session().AttemptVersion);
        Assert.Equal(winningEvents, fixture.Events());
        Assert.Equal(duringBroadcast ? 1 : 0, fixture.Fallback.Calls.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletedSessionCannotCancelOrCloseAfterAwait(bool duringBroadcast)
    {
        using var fixture = new ReceiverFixture();
        await PrepareBudgetCancellationAsync(fixture);
        var removed = false;
        void RemoveSession() => removed = fixture.Store.RemoveSession("invoice");
        if (duringBroadcast)
            fixture.Fallback.RespondAsync = (_, _) => { RemoveSession(); return Task.FromResult<BroadcastResult?>(new BroadcastResult(true)); };
        else
            fixture.Wallet.BeforeInputLookup = () => { RemoveSession(); return Task.CompletedTask; };

        await fixture.Processor.ProcessTickAsync(CancellationToken.None);

        Assert.True(removed);
        Assert.False(fixture.Store.TryGetSession("invoice", out _));
        Assert.Equal(duringBroadcast ? 1 : 0, fixture.Fallback.Calls.Count);
        Assert.Equal(0, fixture.Inputs.ContributionCalls);
        Assert.Equal(0, fixture.ProposalSigner.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingFallbackAfterPreparedProposalRetainsFinancialHistoryAndReservations(bool rejected)
    {
        using var fixture = new ReceiverFixture();
        using var prepared = await fixture.PrepareProposalAsync(contributeReceiver: true);
        var transaction = PSBT.Parse(prepared.Psbt(), Network.RegTest).GetGlobalTransaction();
        var input = new NBitcoin.OutPoint(uint256.One, 0);
        Assert.True(fixture.Store.TryReserveContributedInput(fixture.Store.Owner("invoice"), "store", input, DateTimeOffset.UtcNow.AddMinutes(10)));
        await fixture.RecordExpectedAsync(transaction.GetHash().ToString(), 0, transaction.Outputs[0].Value.Satoshi, CancellationToken.None);
        var bridge = await fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None);
        var context = fixture.Context();
        using var cancellation = prepared.Cancel();
        using var pending = cancellation.Save(context.Persister);
        Assert.NotNull(pending);
        var original = pending.FallbackTx();
        var events = fixture.Events();
        fixture.Fallback.RespondAsync = (_, _) => Task.FromResult<BroadcastResult?>(rejected
            ? new BroadcastResult(false) { RPCCode = RPCErrorCode.RPC_TRANSACTION_ERROR }
            : new BroadcastResult(true));

        await fixture.StateProcessor.ProcessPendingFallbackAsync(context, pending, CancellationToken.None);
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);

        Assert.Equal(original, Assert.Single(fixture.Fallback.Calls));
        Assert.Equal(events, fixture.Events().Take(events.Length));
        if (rejected) AssertReceiverState<ReceiveSession.ReceiverPendingFallback>(fixture);
        else AssertReceiverState<ReceiveSession.Closed>(fixture);
        Assert.False(fixture.Store.RemoveSession("invoice"));
        Assert.False(fixture.Store.TryRemoveSessionUnlessNegotiating("invoice"));
        Assert.Equal(bridge, await fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None));
        using var db = fixture.Factory.CreateContext();
        Assert.Single(db.ReceiverInputReservations);
    }

    [Theory]
    [InlineData("Expired", false)]
    [InlineData("Settled", false)]
    [InlineData("expiry", false)]
    [InlineData("Expired", true)]
    [InlineData("Settled", true)]
    [InlineData("expiry", true)]
    public async Task LifecycleChangeDuringLookupStopsCancellationAndNativeProgress(string change, bool ready)
    {
        using var fixture = new ReceiverFixture();
        await PrepareBudgetCancellationAsync(fixture);
        var before = fixture.Session();
        var events = fixture.Events();
        fixture.Wallet.Status = ready ? PayjoinOwnershipLookupStatus.Ready : PayjoinOwnershipLookupStatus.Exhausted;
        fixture.Wallet.BeforeInputLookup = () =>
        {
            ChangeFallbackLifecycle(fixture, change, fixture.Fallback.Clock.GetUtcNow());
            Assert.Equal(before.AttemptVersion, fixture.Session().AttemptVersion);
            return Task.CompletedTask;
        };

        await fixture.Processor.ProcessTickAsync(CancellationToken.None);

        Assert.Equal(events, fixture.Events());
        AssertReceiverState<ReceiveSession.MaybeInputsOwned>(fixture);
        Assert.Empty(fixture.Fallback.Calls);
        Assert.Equal(0, fixture.Inputs.ContributionCalls);
        Assert.Equal(0, fixture.ProposalSigner.Calls);
    }

    [Theory]
    [InlineData("Expired")]
    [InlineData("Settled")]
    [InlineData("expiry")]
    public async Task LifecycleChangeAfterSavedCancellationPreventsBroadcast(string change)
    {
        var clock = new CallbackTimeProvider();
        using var fixture = new ReceiverFixture(timeProvider: clock);
        await PrepareBudgetCancellationAsync(fixture);
        var changed = false;
        clock.BeforeRead = () =>
        {
            if (changed) return;
            using var replay = fixture.Replay();
            using var state = replay.State();
            if (state is not ReceiveSession.ReceiverPendingFallback) return;
            changed = true;
            var version = fixture.Session().AttemptVersion;
            ChangeFallbackLifecycle(fixture, change, clock.Now);
            Assert.Equal(version, fixture.Session().AttemptVersion);
        };

        await fixture.Processor.ProcessTickAsync(CancellationToken.None);

        Assert.True(changed);
        AssertReceiverState<ReceiveSession.ReceiverPendingFallback>(fixture);
        Assert.Empty(fixture.Fallback.Calls);
    }

    [Theory]
    [InlineData("missing-inputs")]
    [InlineData("rejected")]
    [InlineData("network")]
    public async Task FallbackCooldownSurvivesTicksAndAttemptVersionChanges(string outcome)
    {
        using var fixture = new ReceiverFixture();
        await PrepareBudgetCancellationAsync(fixture);
        var bridge = await fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None);
        fixture.Fallback.RespondAsync = (_, _) => outcome == "network"
            ? throw new HttpRequestException("Lost response.")
            : Task.FromResult<BroadcastResult?>(new BroadcastResult(false)
            {
                RPCCode = outcome == "missing-inputs" ? RPCErrorCode.RPC_TRANSACTION_ERROR : RPCErrorCode.RPC_TRANSACTION_REJECTED
            });
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        AssertReceiverState<ReceiveSession.ReceiverPendingFallback>(fixture);
        var ownershipCalls = fixture.Wallet.InputLookupCalls;
        using (var db = fixture.Factory.CreateContext())
        {
            db.ReceiverSessions.Single().AttemptVersion++;
            db.SaveChanges();
        }
        fixture.Fallback.RespondAsync = (_, _) => Task.FromResult<BroadcastResult?>(new BroadcastResult(true));
        for (var tick = 0; tick < 3; tick++) await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        fixture.Fallback.Clock.Advance(PayjoinReceiverStateProcessor.FallbackRetryDelay - TimeSpan.FromMilliseconds(1));
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        Assert.Single(fixture.Fallback.Calls);
        fixture.Fallback.Clock.Advance(TimeSpan.FromMilliseconds(1));

        await fixture.Processor.ProcessTickAsync(CancellationToken.None);

        AssertReceiverState<ReceiveSession.Closed>(fixture);
        Assert.Equal(2, fixture.Fallback.Calls.Count);
        Assert.Equal(fixture.Fallback.Calls[0], fixture.Fallback.Calls[1]);
        Assert.Equal(ownershipCalls, fixture.Wallet.InputLookupCalls);
        Assert.Equal(0, fixture.Inputs.ContributionCalls);
        Assert.Equal(0, fixture.ProposalSigner.Calls);
        Assert.Equal(bridge, await fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None));
    }

    [Theory]
    [InlineData("Expired")]
    [InlineData("Settled")]
    [InlineData("expiry")]
    public async Task LifecycleChangeDuringFallbackCooldownStopsFurtherBroadcasts(string change)
    {
        using var fixture = new ReceiverFixture();
        await PrepareBudgetCancellationAsync(fixture);
        fixture.Fallback.RespondAsync = (_, _) => Task.FromResult<BroadcastResult?>(new BroadcastResult(false)
        {
            RPCCode = RPCErrorCode.RPC_TRANSACTION_REJECTED
        });
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        ChangeFallbackLifecycle(fixture, change, fixture.Fallback.Clock.GetUtcNow());
        fixture.Fallback.Clock.Advance(PayjoinReceiverStateProcessor.FallbackRetryDelay);

        await fixture.Processor.ProcessTickAsync(CancellationToken.None);

        AssertReceiverState<ReceiveSession.ReceiverPendingFallback>(fixture);
        Assert.Single(fixture.Fallback.Calls);
    }

    [Fact]
    public async Task ParallelFallbackCallsShareAdmissionAndCoolDownFromCompletion()
    {
        using var fixture = new ReceiverFixture();
        await PrepareBudgetCancellationAsync(fixture);
        fixture.Fallback.RespondAsync = (_, _) => Task.FromResult<BroadcastResult?>(new BroadcastResult(false));
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        fixture.Fallback.Clock.Advance(PayjoinReceiverStateProcessor.FallbackRetryDelay);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Fallback.RespondAsync = async (_, token) =>
        {
            started.SetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            return new BroadcastResult(false) { RPCCode = RPCErrorCode.RPC_TRANSACTION_ERROR };
        };
        var first = fixture.Processor.ProcessTickAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        fixture.Fallback.Clock.Advance(TimeSpan.FromSeconds(45));
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        Assert.Equal(2, fixture.Fallback.Calls.Count);
        release.SetResult();
        await first.ConfigureAwait(true);
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        Assert.Equal(2, fixture.Fallback.Calls.Count);
        fixture.Fallback.Clock.Advance(PayjoinReceiverStateProcessor.FallbackRetryDelay);
        fixture.Fallback.RespondAsync = (_, _) => Task.FromResult<BroadcastResult?>(new BroadcastResult(true));
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        Assert.Equal(3, fixture.Fallback.Calls.Count);
        AssertReceiverState<ReceiveSession.Closed>(fixture);
    }

    private static void ChangeFallbackLifecycle(ReceiverFixture fixture, string change, DateTimeOffset now)
    {
        if (change == "expiry")
        {
            using var db = fixture.Factory.CreateContext();
            db.ReceiverSessions.Single().MonitoringExpiresAt = now;
            db.SaveChanges();
        }
        else Assert.True(fixture.Store.RequestClose("invoice", Enum.Parse<InvoiceStatus>(change), fixture.Session().SessionInstanceId));
    }

    private sealed class CallbackTimeProvider : TimeProvider
    {
        internal DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;
        internal Action? BeforeRead { get; set; }
        public override DateTimeOffset GetUtcNow() { BeforeRead?.Invoke(); return Now; }
    }

    private static async Task PrepareBudgetCancellationAsync(ReceiverFixture fixture)
    {
        fixture.Wallet.Status = PayjoinOwnershipLookupStatus.Deferred;
        await fixture.PostOriginalAsync().ConfigureAwait(true);
        await fixture.Processor.ProcessTickAsync(CancellationToken.None).ConfigureAwait(true);
        AssertReceiverState<ReceiveSession.MaybeInputsOwned>(fixture);
        fixture.Wallet.Status = PayjoinOwnershipLookupStatus.Exhausted;
    }

    private static byte[] OriginalFallback(ReceiverFixture fixture)
    {
        using var replay = fixture.Replay();
        using var history = replay.SessionHistory();
        return Assert.IsType<byte[]>(history.FallbackTx());
    }

    private static void AssertReceiverState<T>(ReceiverFixture fixture)
    {
        using var replay = fixture.Replay();
        using var state = replay.State();
        Assert.IsType<T>(state);
    }

    private static RPCException RpcFailure(RPCErrorCode code) => new(code, "Injected RPC failure.", null!);

    private sealed class RecordingFallback
    {
        public List<byte[]> Calls { get; } = [];
        public FallbackLog Log { get; } = new();
        public PayjoinOwnershipLookupBudgetTests.ManualTimeProvider Clock { get; } = new();
        public Func<byte[], CancellationToken, Task<BroadcastResult?>> RespondAsync { get; set; }
            = (_, _) => Task.FromResult<BroadcastResult?>(new BroadcastResult(true));
        public Task<BroadcastResult?> BroadcastAsync(byte[] original, CancellationToken token)
        {
            Calls.Add(original.ToArray());
            return RespondAsync(original, token);
        }
    }

    private sealed class FallbackLog : ILogger<PayjoinReceiverStateProcessor>
    {
        public List<int> Events { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Events.Add(eventId.Id);
    }
}
