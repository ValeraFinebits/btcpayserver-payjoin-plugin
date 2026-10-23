using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinWalletOwnershipServiceTests
{
    [Fact]
    public async Task RetryAfterTheLastLookupFailsCanCompleteAFullPass()
    {
        using var fixture = new LookupFixture(coldWallet: true);
        var scripts = Enumerable.Range(1, 7).Select(index => new[] { (byte)(0x50 + index) }).ToArray();
        var original = Original(24, scripts);
        fixture.Handler.RespondAsync = (_, _) => fixture.Handler.Calls.Count == 62
            ? throw new HttpRequestException("One temporary failure on the final lookup.")
            : Task.FromResult(NotFound());

        Assert.Null(await fixture.ResolveAsync(original));
        fixture.Clock.Advance(PayjoinOwnershipLookupStateStore.RetryDelay);
        fixture.Handler.RespondAsync = (_, _) => Task.FromResult(NotFound());

        Assert.NotNull(await fixture.ResolveAsync(original));
        Assert.Equal(124, fixture.Handler.Calls.Count);
    }

    [Fact]
    public async Task InputAndOutputChecksShareOneRequestBudgetAcrossTicks()
    {
        var logger = new RecordingLogger();
        using var fixture = new LookupFixture(coldWallet: true, logger: logger);
        var original = Original(PayjoinWalletOwnershipService.MaxRequestsPerPass / 2 - 1,
            [new byte[] { 0x51 }, new byte[] { 0x52 }]);

        Assert.Null(await fixture.ResolveAsync(original));
        Assert.Equal(PayjoinWalletOwnershipService.MaxRequestsPerPass, fixture.Handler.Calls.Count);
        for (var tick = 0; tick < 10; tick++)
        {
            fixture.Clock.Advance(PayjoinOwnershipLookupStateStore.RetryDelay);
            Assert.Null(await fixture.ResolveAsync(original));
        }
        Assert.Equal(PayjoinWalletOwnershipService.MaxRequestsPerPass, fixture.Handler.Calls.Count);
        Assert.Equal(2, Assert.Single(logger.Entries).EventId.Id);
    }

    [Fact]
    public async Task ProposalFromIssue192FitsWithinTheBound()
    {
        using var fixture = new LookupFixture(coldWallet: true);
        var scripts = Enumerable.Range(1, 7).Select(index => new[] { (byte)(0x50 + index) }).ToArray();

        var resolver = await fixture.ResolveAsync(Original(24, scripts));

        Assert.NotNull(resolver);
        Assert.Equal(62, fixture.Handler.Calls.Count);
    }

    [Fact]
    public async Task RepeatedOutpointsAndScriptsDoNotRepeatRequests()
    {
        using var fixture = new LookupFixture();
        var outpoint = new OutPoint(uint256.One, 0);
        var original = new PayjoinOriginalTransactionFacts(uint256.One,
            [outpoint, outpoint, new OutPoint(uint256.One, 1)],
            [LookupFixture.ReceiverScript, LookupFixture.ReceiverScript, new byte[] { 0x51 }, new byte[] { 0x51 }]);

        var resolver = await fixture.ResolveAsync(original);

        Assert.NotNull(resolver);
        Assert.Equal(2, fixture.Handler.Calls.Count);
        Assert.True(resolver.Outputs.IsOwned(LookupFixture.ReceiverScript));
        Assert.False(resolver.Inputs.IsOwned(uint256.One.ToString(), 0));
        Assert.False(resolver.Outputs.IsOwned(new byte[] { 0x51 }));
    }

    [Fact]
    public async Task DeadlineCoversBothInputAndOutputRequests()
    {
        using var fixture = new LookupFixture(lookupTimeout: TimeSpan.FromMilliseconds(200));
        fixture.Handler.RespondAsync = async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(130), token).ConfigureAwait(false);
            return NotFound();
        };

        Assert.Null(await fixture.ResolveAsync(Original(1, [new byte[] { 0x51 }]))
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        Assert.InRange(fixture.Handler.Calls.Count, 1, 2);
        Assert.Equal(0, fixture.Handler.Active);
    }

    [Fact]
    public async Task DependencyFailuresHaveBackoffAndAFiniteRetryCount()
    {
        var logger = new RecordingLogger();
        using var fixture = new LookupFixture(logger: logger);
        fixture.Handler.RespondAsync = (_, _) => throw new HttpRequestException("NBXplorer is unavailable.");
        var original = Original(1);

        for (var attempt = 1; attempt <= PayjoinOwnershipLookupStateStore.MaxPasses; attempt++)
        {
            Assert.Null(await fixture.ResolveAsync(original));
            Assert.Equal(attempt, fixture.Handler.Calls.Count);
            Assert.Null(await fixture.ResolveAsync(original));
            Assert.Equal(attempt, fixture.Handler.Calls.Count);
            fixture.Clock.Advance(PayjoinOwnershipLookupStateStore.RetryDelay);
        }
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Null(await fixture.ResolveAsync(original));
        Assert.Equal(PayjoinOwnershipLookupStateStore.MaxPasses, fixture.Handler.Calls.Count);
        Assert.Single(logger.Entries, entry => entry.EventId.Id == 2);
    }

    [Fact]
    public async Task NbxplorerServiceUnavailableDefersWithoutTreatingTheProposalAsInvalid()
    {
        using var fixture = new LookupFixture();
        fixture.Handler.RespondAsync = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("{\"httpCode\":503,\"code\":\"unavailable\",\"message\":\"Indexer is starting.\"}",
                Encoding.UTF8, "application/json")
        });
        var original = Original(1);

        Assert.Null(await fixture.ResolveAsync(original));
        Assert.Null(await fixture.ResolveAsync(original));
        Assert.Single(fixture.Handler.Calls);
    }

    [Fact]
    public async Task RetryReusesMatchedOutpointsButRefreshesMissingHistory()
    {
        using var fixture = new LookupFixture();
        var original = Original(2, [new byte[] { 0x51 }]);
        var firstId = original.InputOutpoints[0].Hash.ToString();
        var secondId = original.InputOutpoints[1].Hash.ToString();
        var retry = false;
        fixture.Handler.RespondAsync = (request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains(firstId, StringComparison.Ordinal) || retry && path.Contains(secondId, StringComparison.Ordinal))
                return Task.FromResult(Json("{\"outputs\":[{\"index\":0}]}"));
            if (!retry && !path.Contains(secondId, StringComparison.Ordinal))
                throw new HttpRequestException("Temporary output lookup failure.");
            return Task.FromResult(NotFound());
        };

        Assert.Null(await fixture.ResolveAsync(original));
        retry = true;
        fixture.Clock.Advance(PayjoinOwnershipLookupStateStore.RetryDelay);
        var resolver = await fixture.ResolveAsync(original);

        Assert.NotNull(resolver);
        Assert.True(resolver.Inputs.IsOwned(firstId, 0));
        Assert.True(resolver.Inputs.IsOwned(secondId, 0));
        Assert.Single(fixture.Handler.Calls, path => path.Contains(firstId, StringComparison.Ordinal));
        Assert.Equal(2, fixture.Handler.Calls.Count(path => path.Contains(secondId, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RetryReusesKnownWalletOutputScripts()
    {
        using var fixture = new LookupFixture();
        var retry = false;
        fixture.Handler.RespondAsync = (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/51", StringComparison.Ordinal))
                return Task.FromResult(Json("{\"scriptPubKey\":\"51\",\"keyPath\":\"0/0\"}"));
            if (!retry) throw new HttpRequestException("Temporary output lookup failure.");
            return Task.FromResult(NotFound());
        };
        var original = Original(0, [new byte[] { 0x51 }, new byte[] { 0x52 }]);

        Assert.Null(await fixture.ResolveAsync(original));
        retry = true;
        fixture.Clock.Advance(PayjoinOwnershipLookupStateStore.RetryDelay);
        var resolver = await fixture.ResolveAsync(original);

        Assert.NotNull(resolver);
        Assert.True(resolver.Outputs.IsOwned(new byte[] { 0x51 }));
        Assert.False(resolver.Outputs.IsOwned(new byte[] { 0x52 }));
        Assert.Equal(3, fixture.Handler.Calls.Count);
    }

    [Fact]
    public async Task ShutdownCancellationPropagatesAndReleasesThePassSlotWithoutResettingItsBudget()
    {
        using var fixture = new LookupFixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.RespondAsync = async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return NotFound();
        };
        var original = Original(1);
        var resolving = fixture.Service.CreateResolverAsync(fixture.Session, original, LookupFixture.ReceiverScript, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Null(await fixture.ResolveAsync(original));
        Assert.Single(fixture.Handler.Calls);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolving);
        fixture.Handler.RespondAsync = (_, _) => Task.FromResult(NotFound());

        Assert.NotNull(await fixture.ResolveAsync(original));
        Assert.Equal(0, fixture.Handler.Active);
        Assert.Equal(2, fixture.Handler.Calls.Count);
        Assert.NotNull(await fixture.ResolveAsync(original));
        Assert.Equal(PayjoinOwnershipLookupStatus.Exhausted, (await fixture.LookupAsync(original)).Status);
        Assert.Equal(3, fixture.Handler.Calls.Count);
    }

    [Fact]
    public async Task BlockedLookupsReleaseReceiverWorkersForAnotherSession()
    {
        using var fixture = new LookupFixture(lookupTimeout: TimeSpan.FromMilliseconds(300));
        using var storeFixture = new SessionStoreFixture();
        var store = storeFixture.CreateStore();
        var blockedSessionCount = PayjoinReceiverSessionProcessor.MaxConcurrentReceiverSessions;
        for (var index = 0; index < blockedSessionCount; index++)
            store.GetOrCreateSession($"blocked-{index}", string.Empty, "store", DateTimeOffset.UtcNow.AddMinutes(10), ["bootstrap"]);
        store.GetOrCreateSession("healthy", string.Empty, "store", DateTimeOffset.UtcNow.AddMinutes(10), ["bootstrap"]);
        var healthyOriginal = Original(1) with { TransactionId = uint256.Zero, InputOutpoints = [new OutPoint(uint256.Zero, 0)] };
        fixture.Handler.RespondAsync = async (request, token) =>
        {
            if (!request.RequestUri!.AbsolutePath.Contains(uint256.Zero.ToString(), StringComparison.Ordinal))
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return NotFound();
        };
        var guard = new OwnershipGuard(fixture.Service, healthyOriginal);
        var processor = new PayjoinReceiverSessionProcessor(store, guard, null!, null!, null!,
            null!, null!, null!, null!, null!, NullLogger<PayjoinReceiverSessionProcessor>.Instance);

        var maxTicks = (blockedSessionCount + PayjoinOwnershipLookupStateStore.MaxConcurrentPasses - 1)
            / PayjoinOwnershipLookupStateStore.MaxConcurrentPasses + 1;
        for (var tick = 0; tick < maxTicks && !guard.HealthyCompleted; tick++)
            await processor.ProcessTickAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(guard.HealthyCompleted);
        Assert.Equal(PayjoinOwnershipLookupStateStore.MaxConcurrentPasses, fixture.Handler.MaxActive);
        Assert.Equal(0, fixture.Handler.Active);
        Assert.Equal(blockedSessionCount + 1, store.GetSessions().Count);
    }

    [Fact]
    public async Task ReplacementSessionAndWalletConfigurationHaveSeparateBudgets()
    {
        using var fixture = new LookupFixture();
        fixture.Handler.RespondAsync = (_, _) => throw new HttpRequestException("Unavailable.");
        var original = Original(1);
        for (var index = 0; index < PayjoinOwnershipLookupStateStore.MaxPasses; index++)
        {
            Assert.Null(await fixture.ResolveAsync(original));
            fixture.Clock.Advance(PayjoinOwnershipLookupStateStore.RetryDelay);
        }
        fixture.Handler.RespondAsync = (_, _) => Task.FromResult(NotFound());
        Assert.Null(await fixture.ResolveAsync(original));

        var replacement = fixture.CreateSession(fixture.Session.InvoiceId);
        Assert.NotNull(await fixture.ResolveAsync(original, replacement));
        fixture.Wallet = fixture.Wallet with { ColdWalletDerivation = fixture.ColdDerivation };
        Assert.NotNull(await fixture.ResolveAsync(original));
    }

    [Fact]
    public async Task FullCacheDoesNotEvictAndResetAnExistingRetryBudget()
    {
        using var fixture = new LookupFixture();
        fixture.Handler.RespondAsync = (_, _) => throw new HttpRequestException("Unavailable.");
        var original = Original(1);
        for (var index = 0; index < PayjoinOwnershipLookupStateStore.MaxPasses; index++)
        {
            Assert.Null(await fixture.ResolveAsync(original));
            fixture.Clock.Advance(PayjoinOwnershipLookupStateStore.RetryDelay);
        }
        for (var index = 1; index < PayjoinOwnershipLookupStateStore.MaxCachedProposals; index++)
            Assert.NotNull(await fixture.ResolveAsync(Original(0), fixture.CreateSession($"other-{index}")));

        Assert.Null(await fixture.ResolveAsync(original, fixture.CreateSession("over-capacity")));
        Assert.Null(await fixture.ResolveAsync(original));
        Assert.Equal(PayjoinOwnershipLookupStateStore.MaxPasses, fixture.Handler.Calls.Count);
    }

    private static PayjoinOriginalTransactionFacts Original(int distinctTransactions, IReadOnlyList<byte[]>? outputs = null)
        => new(uint256.One, Enumerable.Range(1, distinctTransactions)
            .Select(index => new OutPoint(new uint256((ulong)index), 0)).ToArray(), outputs ?? []);

    [Fact]
    public async Task ThirdFullPassMaySucceedButAFourthPassCannotStart()
    {
        using var fixture = new LookupFixture(coldWallet: true);
        var requestsPerPass = PayjoinWalletOwnershipService.MaxRequestsPerPass;
        var original = Original(requestsPerPass / 2 - 1, [new byte[] { 0x51 }]); // Exactly one full hot+cold budget.
        fixture.Handler.RespondAsync = (_, _) => fixture.Handler.Calls.Count % requestsPerPass == 0
            && fixture.Handler.Calls.Count / requestsPerPass < PayjoinOwnershipLookupStateStore.MaxPasses
            ? throw new HttpRequestException("Temporary final-operation failure.")
            : Task.FromResult(NotFound());
        for (var pass = 0; pass < PayjoinOwnershipLookupStateStore.MaxPasses - 1; pass++)
        {
            Assert.Equal(PayjoinOwnershipLookupStatus.Deferred, (await fixture.LookupAsync(original)).Status);
            Assert.Equal((pass + 1) * requestsPerPass, fixture.Handler.Calls.Count);
            fixture.Clock.Advance(PayjoinOwnershipLookupStateStore.RetryDelay);
        }
        Assert.Equal(PayjoinOwnershipLookupStatus.Ready, (await fixture.LookupAsync(original)).Status);
        Assert.Equal(PayjoinOwnershipLookupStateStore.MaxPasses * requestsPerPass, fixture.Handler.Calls.Count);
        Assert.Equal(PayjoinOwnershipLookupStatus.Exhausted, (await fixture.LookupAsync(original)).Status);
        Assert.Equal(PayjoinOwnershipLookupStateStore.MaxPasses * requestsPerPass, fixture.Handler.Calls.Count);
    }

    [Fact]
    public async Task SuccessfulPassExhaustionIsLoggedOnceAcrossTicksAndConcurrentCalls()
    {
        var logger = new RecordingLogger();
        using var fixture = new LookupFixture(logger: logger);
        var original = Original(1);
        for (var pass = 0; pass < PayjoinOwnershipLookupStateStore.MaxPasses; pass++)
            Assert.Equal(PayjoinOwnershipLookupStatus.Ready, (await fixture.LookupAsync(original)).Status);
        Assert.Empty(logger.Entries);

        var loaded = new TaskCompletionSource<PayjoinWalletOwnershipService.WalletContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.LoadWallet = () => loaded.Task;
        var concurrentCalls = Enumerable.Range(0, 16).Select(_ => fixture.LookupAsync(original)).ToArray();
        loaded.SetResult(fixture.Wallet);
        Assert.All(await Task.WhenAll(concurrentCalls), result => Assert.Equal(PayjoinOwnershipLookupStatus.Exhausted, result.Status));
        fixture.LoadWallet = null;
        for (var tick = 0; tick < 3; tick++)
        {
            fixture.Clock.Advance(PayjoinOwnershipLookupStateStore.RetryDelay);
            Assert.Equal(PayjoinOwnershipLookupStatus.Exhausted, (await fixture.LookupAsync(original)).Status);
        }

        Assert.Equal(PayjoinOwnershipLookupStateStore.MaxPasses, fixture.Handler.Calls.Count);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(2, entry.EventId.Id);
    }

    [Fact]
    public async Task HealthyConcurrentProposalsCompleteInTwoAdmissionRounds()
    {
        using var fixture = new LookupFixture(coldWallet: true);
        var scripts = Enumerable.Range(1, 7).Select(index => new[] { (byte)(0x50 + index) }).ToArray();
        var original = Original(24, scripts);
        var sessions = Enumerable.Range(0, 2 * PayjoinOwnershipLookupStateStore.MaxConcurrentPasses)
            .Select(i => fixture.CreateSession($"healthy-{i}")).ToArray();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        fixture.Handler.RespondAsync = async (_, token) =>
        {
            if (Interlocked.Increment(ref started) == PayjoinOwnershipLookupStateStore.MaxConcurrentPasses) allStarted.TrySetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            return NotFound();
        };
        var first = sessions.Select(session => fixture.LookupAsync(original, session)).ToArray();
        await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(PayjoinOwnershipLookupStateStore.MaxConcurrentPasses, first.Count(task => task.IsCompletedSuccessfully && task.Result.Status == PayjoinOwnershipLookupStatus.Deferred));
        Assert.Equal(PayjoinOwnershipLookupStateStore.MaxConcurrentPasses, fixture.Handler.Active);
        release.TrySetResult();
        var results = await Task.WhenAll(first);
        Assert.Equal(PayjoinOwnershipLookupStateStore.MaxConcurrentPasses, results.Count(result => result.Status == PayjoinOwnershipLookupStatus.Ready));
        for (var i = 0; i < sessions.Length; i++)
            if (results[i].Status == PayjoinOwnershipLookupStatus.Deferred)
                Assert.Equal(PayjoinOwnershipLookupStatus.Ready, (await fixture.LookupAsync(original, sessions[i])).Status);
        Assert.Equal(sessions.Length * 62, fixture.Handler.Calls.Count);
        Assert.Equal(PayjoinOwnershipLookupStateStore.MaxConcurrentPasses, fixture.Handler.MaxActive);
        foreach (var session in sessions)
        {
            Assert.NotNull(await fixture.ResolveAsync(original, session));
            Assert.NotNull(await fixture.ResolveAsync(original, session));
            Assert.Equal(PayjoinOwnershipLookupStatus.Exhausted, (await fixture.LookupAsync(original, session)).Status);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletingSessionsFreesCapacityForAnotherStore(bool discard)
    {
        using var fixture = new LookupFixture();
        using var database = new SessionStoreFixture();
        var store = database.CreateStore(fixture.States);
        var sessions = Enumerable.Range(0, PayjoinOwnershipLookupStateStore.MaxCachedProposals)
            .Select(i => store.GetOrCreateSession($"completed-{i}", "receiver", "store", fixture.Clock.GetUtcNow().AddHours(1), ["bootstrap"]))
            .ToArray();
        foreach (var session in sessions)
        {
            store.StorePayjoinUri(session.InvoiceId, "receiver", "bitcoin:receiver?pj=example");
            Assert.True(store.TryGetSession(session.InvoiceId, out var persisted));
            Assert.NotNull(await fixture.ResolveAsync(Original(1), persisted));
        }
        var otherStore = new PayjoinReceiverSessionState("new", "another-store", "", fixture.Clock.GetUtcNow().AddHours(1), fixture.Clock.GetUtcNow(), fixture.Clock.GetUtcNow());
        Assert.Equal(PayjoinOwnershipLookupStatus.CapacityUnavailable, (await fixture.LookupAsync(Original(1), otherStore)).Status);
        foreach (var session in sessions)
            Assert.True(discard ? store.TryRemoveSessionUnlessNegotiating(session.InvoiceId) : store.RemoveSession(session.InvoiceId));
        Assert.Empty(store.GetSessions());
        Assert.NotNull(await fixture.ResolveAsync(Original(1), otherStore));
    }

    [Fact]
    public async Task ExpiredEntriesReleaseCapacityBeforeAdmittingAnotherProposal()
    {
        using var fixture = new LookupFixture();
        for (var i = 0; i < PayjoinOwnershipLookupStateStore.MaxCachedProposals; i++)
            Assert.NotNull(await fixture.ResolveAsync(Original(0), fixture.CreateSession($"expired-{i}")));
        fixture.Clock.Advance(TimeSpan.FromHours(2));
        Assert.NotNull(await fixture.ResolveAsync(Original(1), fixture.CreateSession("new")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletionBeforeLookupRegistrationRejectsOldGenerationsWithoutUsingCapacity(bool discard)
    {
        using var fixture = new LookupFixture();
        using var database = new SessionStoreFixture();
        var store = database.CreateStore(fixture.States);
        fixture.IsSessionCurrent = store.IsCurrentSession;
        var walletLoads = 0;
        fixture.LoadWallet = () => { walletLoads++; return Task.FromResult(fixture.Wallet); };
        var original = Original(1);
        var sessions = Enumerable.Range(0, PayjoinOwnershipLookupStateStore.MaxCachedProposals)
            .Select(i => store.GetOrCreateSession($"deleted-{i}", "receiver", "store", fixture.Clock.GetUtcNow().AddHours(1), ["bootstrap"]))
            .ToArray();
        foreach (var session in sessions)
        {
            Assert.True(discard ? store.TryRemoveSessionUnlessNegotiating(session.InvoiceId) : store.RemoveSession(session.InvoiceId));
            Assert.Equal(PayjoinOwnershipLookupStatus.Exhausted, (await fixture.LookupAsync(original, session)).Status);
        }
        Assert.Empty(store.GetSessions());
        Assert.Empty(fixture.Handler.Calls);
        Assert.Equal(0, walletLoads);

        var oldSession = sessions[0];
        var replacement = store.GetOrCreateSession(oldSession.InvoiceId, "receiver", "store", fixture.Clock.GetUtcNow().AddHours(1), ["bootstrap"]);
        Assert.NotEqual(oldSession.CreatedAt, replacement.CreatedAt);
        Assert.NotNull(await fixture.ResolveAsync(original, replacement));
        Assert.Equal(PayjoinOwnershipLookupStatus.Exhausted, (await fixture.LookupAsync(original, oldSession)).Status);
        Assert.NotNull(await fixture.ResolveAsync(original, replacement));
        Assert.NotNull(await fixture.ResolveAsync(original, replacement));
        Assert.Equal(PayjoinOwnershipLookupStatus.Exhausted, (await fixture.LookupAsync(original, replacement)).Status);
        var otherStore = store.GetOrCreateSession("healthy", "receiver", "another-store", fixture.Clock.GetUtcNow().AddHours(1), ["bootstrap"]);
        Assert.NotNull(await fixture.ResolveAsync(original, otherStore));
        Assert.Equal(4, fixture.Handler.Calls.Count);
    }

    [Fact]
    public async Task DeletionDuringGenerationCheckIsFencedBeforeLoadingTheWallet()
    {
        using var fixture = new LookupFixture();
        using var database = new SessionStoreFixture();
        var store = database.CreateStore(fixture.States);
        var session = store.GetOrCreateSession("deleted-during-check", "receiver", "store", fixture.Clock.GetUtcNow().AddHours(1), ["bootstrap"]);
        fixture.IsSessionCurrent = key =>
        {
            var current = store.IsCurrentSession(key);
            Assert.True(current);
            Assert.True(store.RemoveSession(key.InvoiceId));
            return current;
        };
        fixture.LoadWallet = () => throw new InvalidOperationException("A retired lookup must not load its wallet.");

        Assert.Equal(PayjoinOwnershipLookupStatus.Exhausted, (await fixture.LookupAsync(Original(1), session)).Status);
        Assert.Empty(fixture.Handler.Calls);
        Assert.Empty(store.GetSessions());
    }

    [Fact]
    public async Task DeletionDuringWalletLoadingFencesTheOldGenerationOnly()
    {
        using var fixture = new LookupFixture();
        var loaded = new TaskCompletionSource<PayjoinWalletOwnershipService.WalletContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.LoadWallet = () => loaded.Task;
        var oldLookup = fixture.LookupAsync(Original(1));
        fixture.States.Forget(PayjoinOwnershipSessionKey.From(fixture.Session));
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.LoadWallet = null;
        var replacement = fixture.CreateSession(fixture.Session.InvoiceId);
        Assert.NotNull(await fixture.ResolveAsync(Original(1), replacement));
        loaded.TrySetResult(fixture.Wallet);
        Assert.Equal(PayjoinOwnershipLookupStatus.Exhausted, (await oldLookup.ConfigureAwait(true)).Status);
        Assert.Single(fixture.Handler.Calls);
        fixture.States.Forget(PayjoinOwnershipSessionKey.From(fixture.Session));
        Assert.NotNull(await fixture.ResolveAsync(Original(1), replacement));
        Assert.NotNull(await fixture.ResolveAsync(Original(1), replacement));
        Assert.Equal(PayjoinOwnershipLookupStatus.Exhausted, (await fixture.LookupAsync(Original(1), replacement)).Status);
    }

    [Fact]
    public async Task DeletingARunningLookupPreventsReadyAndReleasesItsSlot()
    {
        using var fixture = new LookupFixture();
        using var database = new SessionStoreFixture();
        var store = database.CreateStore(fixture.States);
        var session = store.GetOrCreateSession("running", "", "store", fixture.Clock.GetUtcNow().AddHours(1), ["bootstrap"]);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.RespondAsync = async (_, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            return NotFound();
        };
        var lookup = fixture.LookupAsync(Original(1), session);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(store.RemoveSession(session.InvoiceId));
        release.TrySetResult();
        Assert.Equal(PayjoinOwnershipLookupStatus.Exhausted, (await lookup.ConfigureAwait(true)).Status);
        fixture.Handler.RespondAsync = (_, _) => Task.FromResult(NotFound());
        Assert.NotNull(await fixture.ResolveAsync(Original(1), fixture.CreateSession("healthy")));
        Assert.Equal(0, fixture.Handler.Active);
    }

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    internal sealed class LookupFixture : IDisposable
    {
        internal static readonly byte[] ReceiverScript = [0x60];
        private readonly HttpClient _http;
        internal RecordingHandler Handler { get; } = new();
        internal ManualTimeProvider Clock { get; } = new();
        internal PayjoinWalletOwnershipService Service { get; }
        internal PayjoinOwnershipLookupStateStore States { get; }
        internal Func<Task<PayjoinWalletOwnershipService.WalletContext>>? LoadWallet { get; set; }
        internal Func<PayjoinOwnershipSessionKey, bool> IsSessionCurrent { get; set; } = _ => true;
        internal PayjoinWalletOwnershipService.WalletContext Wallet { get; set; }
        internal DerivationStrategyBase ColdDerivation { get; }
        internal PayjoinReceiverSessionState Session { get; }

        internal LookupFixture(bool coldWallet = false, TimeSpan? lookupTimeout = null,
            ILogger<PayjoinWalletOwnershipService>? logger = null)
        {
            _http = new HttpClient(Handler) { Timeout = Timeout.InfiniteTimeSpan };
            var network = new NBXplorerNetworkProvider(ChainName.Regtest).GetBTC();
            var client = network.CreateExplorerClient(new Uri("http://nbxplorer.example/"));
            client.SetClient(_http);
            client.SetNoAuth();
            var hot = network.DerivationStrategyFactory.CreateDirectDerivationStrategy(new ExtKey().Neuter());
            ColdDerivation = network.DerivationStrategyFactory.CreateDirectDerivationStrategy(new ExtKey().Neuter());
            Wallet = new PayjoinWalletOwnershipService.WalletContext(client, hot, coldWallet ? ColdDerivation : null);
            States = new PayjoinOwnershipLookupStateStore(Clock);
            Service = new PayjoinWalletOwnershipService(_ => LoadWallet?.Invoke() ?? Task.FromResult(Wallet), States,
                session => IsSessionCurrent(session), logger, lookupTimeout: lookupTimeout);
            Session = CreateSession("invoice");
        }

        internal PayjoinReceiverSessionState CreateSession(string invoiceId)
            => new(invoiceId, "store", string.Empty, Clock.GetUtcNow().AddHours(1), Clock.GetUtcNow(), Clock.GetUtcNow());

        internal async Task<PayjoinOwnershipResolver?> ResolveAsync(PayjoinOriginalTransactionFacts original,
            PayjoinReceiverSessionState? session = null)
            => (await LookupAsync(original, session).ConfigureAwait(false)).Resolver;

        internal Task<PayjoinOwnershipLookupResult> LookupAsync(PayjoinOriginalTransactionFacts original,
            PayjoinReceiverSessionState? session = null)
            => Service.CreateResolverAsync(session ?? Session, original, ReceiverScript, TestContext.Current.CancellationToken);

        public void Dispose()
        {
            _http.Dispose();
        }
    }

    private sealed class RecordingLogger : ILogger<PayjoinWalletOwnershipService>
    {
        internal ConcurrentQueue<(LogLevel Level, EventId EventId)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Enqueue((logLevel, eventId));
    }

    internal sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly object _sync = new();
        internal List<string> Calls { get; } = [];
        internal int Active { get; private set; }
        internal int MaxActive { get; private set; }
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> RespondAsync { get; set; }
            = (_, _) => Task.FromResult(NotFound());

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                Calls.Add(request.RequestUri!.AbsolutePath);
                Active++;
                MaxActive = Math.Max(MaxActive, Active);
            }
            try
            {
                return await RespondAsync(request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lock (_sync) Active--;
            }
        }
    }

    internal sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private sealed class OwnershipGuard(PayjoinWalletOwnershipService service, PayjoinOriginalTransactionFacts healthyOriginal)
        : IPayjoinReceiverSessionGuard
    {
        internal bool HealthyCompleted { get; private set; }

        public async Task<PayjoinReceiverSessionGuardResult?> TryPrepareAsync(PayjoinReceiverSessionState session, CancellationToken cancellationToken)
        {
            var healthy = session.InvoiceId == "healthy";
            var resolver = await service.CreateResolverAsync(session, healthy ? healthyOriginal : Original(1),
                LookupFixture.ReceiverScript, cancellationToken).ConfigureAwait(false);
            if (healthy) HealthyCompleted = resolver.Status == PayjoinOwnershipLookupStatus.Ready;
            return null;
        }
    }
}
