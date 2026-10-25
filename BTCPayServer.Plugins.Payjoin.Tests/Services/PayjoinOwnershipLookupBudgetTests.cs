using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinOwnershipLookupBudgetTests
{
    private static readonly int[] ExpectedBackoffSeconds = [30, 60, 120, 240, 300, 300];

    [Fact]
    public async Task ProposalFromIssue192FitsWithinTheBound()
    {
        using var fixture = new LookupFixture();
        var original = fixture.CreateOriginal(24);

        var resolver = await fixture.ResolveAsync(original);

        Assert.NotNull(resolver);
        Assert.All(original.InputOutpoints, input => Assert.Equal(PayjoinInputOwnership.Foreign, resolver.GetOwnership(input)));
        Assert.Equal(1 + 2 * original.InputOutpoints.Count, fixture.Handler.Calls.Count);
    }

    [Fact]
    public async Task RegistrationFundingRpcAndScriptLookupsShareOneBudget()
    {
        using var fixture = new LookupFixture();
        var original = fixture.CreateOriginal((PayjoinWalletOwnershipService.MaxRequestsPerPass - 1) / 2);
        fixture.MissingFunding.Add(original.InputOutpoints[0].Hash);

        Assert.NotNull(await fixture.ResolveAsync(original));
        Assert.Equal(PayjoinWalletOwnershipService.MaxRequestsPerPass, fixture.Handler.Calls.Count);
        Assert.Single(fixture.Handler.Calls, path => path.EndsWith("/rpc", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExceedingTheSharedBudgetIsLoggedOnceAndDoesNotRepeatOnLaterTicks()
    {
        var logger = new RecordingLogger();
        using var fixture = new LookupFixture(logger: logger);
        var original = fixture.CreateOriginal(PayjoinWalletOwnershipService.MaxRequestsPerPass / 2);

        Assert.Equal(PayjoinOwnershipLookupStatus.Exhausted, (await fixture.LookupAsync(original)).Status);
        Assert.Equal(PayjoinWalletOwnershipService.MaxRequestsPerPass, fixture.Handler.Calls.Count);
        for (var tick = 0; tick < 10; tick++)
        {
            fixture.Clock.Advance(PayjoinOwnershipLookupStateStore.RetryDelay);
            Assert.Equal(PayjoinOwnershipLookupStatus.Exhausted, (await fixture.LookupAsync(original)).Status);
        }

        Assert.Equal(PayjoinWalletOwnershipService.MaxRequestsPerPass, fixture.Handler.Calls.Count);
        Assert.Equal(2, Assert.Single(logger.Entries).EventId.Id);
    }

    [Fact]
    public async Task BothCoreFallbackRequestsAreChargedToThePass()
    {
        using var fixture = new LookupFixture { UnconfirmedOnly = true };
        var original = fixture.CreateOriginal(1);
        fixture.MissingFunding.Add(original.InputOutpoints[0].Hash);

        Assert.NotNull(await fixture.ResolveAsync(original));

        Assert.Equal(5, fixture.Handler.Calls.Count);
        Assert.Equal(2, fixture.Handler.Calls.Count(path => path.EndsWith("/rpc", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task BothCoreFallbackRequestsConsumeTheSharedBudgetAtItsBoundary()
    {
        using var fixture = new LookupFixture { UnconfirmedOnly = true };
        var original = fixture.CreateOriginal((PayjoinWalletOwnershipService.MaxRequestsPerPass - 1) / 2);
        fixture.MissingFunding.Add(original.InputOutpoints[0].Hash);

        Assert.Equal(PayjoinOwnershipLookupStatus.Exhausted, (await fixture.LookupAsync(original)).Status);
        Assert.Equal(PayjoinWalletOwnershipService.MaxRequestsPerPass, fixture.Handler.Calls.Count);
        Assert.Equal(2, fixture.Handler.Calls.Count(path => path.EndsWith("/rpc", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RepeatedOutpointsAndScriptsDoNotRepeatRequests()
    {
        using var fixture = new LookupFixture();
        var original = fixture.CreateOriginal(1, sameScript: true, outputsPerFunding: 2);
        original = original with { InputOutpoints = [original.InputOutpoints[0], original.InputOutpoints[0], original.InputOutpoints[1]] };

        var resolver = await fixture.ResolveAsync(original);

        Assert.NotNull(resolver);
        Assert.Equal(3, fixture.Handler.Calls.Count);
        Assert.All(original.InputOutpoints, input => Assert.Equal(PayjoinInputOwnership.Foreign, resolver.GetOwnership(input)));
    }

    [Fact]
    public async Task DeadlineCoversRegistrationAndTheRemainingOwnershipRequests()
    {
        using var fixture = new LookupFixture(lookupTimeout: TimeSpan.FromMilliseconds(200));
        fixture.Handler.RespondAsync = async (request, token) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(130), token).ConfigureAwait(false);
            return await fixture.DefaultResponseAsync(request, token).ConfigureAwait(false);
        };

        Assert.Null(await fixture.ResolveAsync(fixture.CreateOriginal(1))
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.InRange(fixture.Handler.Calls.Count, 1, 2);
        Assert.Equal(0, fixture.Handler.Active);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("503")]
    [InlineData("deadline")]
    [InlineData("incomplete")]
    public async Task TemporaryFailuresRecoverAfterMoreThanThreePassesWithCappedBackoff(string failure)
    {
        var logger = new RecordingLogger();
        using var fixture = new LookupFixture(lookupTimeout: TimeSpan.FromMilliseconds(100), logger: logger);
        fixture.Handler.RespondAsync = async (request, token) =>
        {
            if (failure == "http") throw new HttpRequestException("NBXplorer is unavailable.");
            if (failure == "503") return ServiceUnavailable();
            if (failure == "deadline") await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            if (request.RequestUri!.AbsolutePath.Contains("/scripts/", StringComparison.Ordinal)) return fixture.JsonResponse(null);
            return await fixture.DefaultResponseAsync(request, token).ConfigureAwait(false);
        };
        var original = fixture.CreateOriginal(1);
        foreach (var delaySeconds in ExpectedBackoffSeconds)
        {
            var failed = await fixture.LookupAsync(original);
            Assert.Equal(PayjoinOwnershipLookupStatus.Deferred, failed.Status);
            Assert.Null(failed.Resolver);
            var requests = fixture.Handler.Calls.Count;
            var logs = logger.Entries.Count;
            Assert.Equal(PayjoinOwnershipLookupStatus.Deferred, (await fixture.LookupAsync(original)).Status);
            fixture.Clock.Advance(TimeSpan.FromSeconds(delaySeconds) - TimeSpan.FromMilliseconds(1));
            Assert.Equal(PayjoinOwnershipLookupStatus.Deferred, (await fixture.LookupAsync(original)).Status);
            Assert.Equal(requests, fixture.Handler.Calls.Count);
            Assert.Equal(logs, logger.Entries.Count);
            Assert.Equal(0, fixture.Handler.Active);
            fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
        }
        var requestsPerFailure = failure == "incomplete" ? 3 : 1;
        Assert.Equal(ExpectedBackoffSeconds.Length * requestsPerFailure, fixture.Handler.Calls.Count);
        fixture.Handler.RespondAsync = fixture.DefaultResponseAsync;
        var resolver = await fixture.ResolveAsync(original);
        Assert.NotNull(resolver);
        Assert.Equal(PayjoinInputOwnership.Foreign, resolver.GetOwnership(original.InputOutpoints[0]));
        Assert.Equal(ExpectedBackoffSeconds.Length * requestsPerFailure + 3, fixture.Handler.Calls.Count);
        Assert.Equal(ExpectedBackoffSeconds.Length, logger.Entries.Count);
        Assert.DoesNotContain(logger.Entries, entry => entry.EventId.Id == 2);
    }

    [Fact]
    public async Task NbxplorerServiceUnavailableRetainsTheProposalWithoutRepeatingRequests()
    {
        using var fixture = new LookupFixture();
        fixture.Handler.RespondAsync = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("{\"httpCode\":503,\"code\":\"unavailable\",\"message\":\"Indexer is starting.\"}", Encoding.UTF8, "application/json")
        });
        var original = fixture.CreateOriginal(1);

        Assert.Equal(PayjoinOwnershipLookupStatus.Deferred, (await fixture.LookupAsync(original)).Status);
        Assert.Equal(PayjoinOwnershipLookupStatus.Deferred, (await fixture.LookupAsync(original)).Status);
        Assert.Single(fixture.Handler.Calls);
    }

    [Fact]
    public async Task UnknownAndForeignEvidenceAreRefreshedOnAnotherPass()
    {
        using var fixture = new LookupFixture();
        var original = fixture.CreateOriginal(1);
        var unavailable = true;
        fixture.Handler.RespondAsync = (request, token) => request.RequestUri!.AbsolutePath.Contains("/scripts/", StringComparison.Ordinal) && unavailable
            ? Task.FromResult(fixture.JsonResponse(null))
            : fixture.DefaultResponseAsync(request, token);

        Assert.Equal(PayjoinOwnershipLookupStatus.Deferred, (await fixture.LookupAsync(original)).Status);
        unavailable = false;
        fixture.Clock.Advance(PayjoinOwnershipLookupStateStore.RetryDelay);
        var resolver = await fixture.ResolveAsync(original);
        Assert.NotNull(resolver);
        Assert.Equal(PayjoinInputOwnership.Foreign, resolver.GetOwnership(original.InputOutpoints[0]));
        Assert.Null(await fixture.ResolveAsync(original));
        fixture.Clock.Advance(2 * PayjoinOwnershipLookupStateStore.RetryDelay);
        Assert.NotNull(await fixture.ResolveAsync(original));

        Assert.Equal(3,
            fixture.Handler.Calls.Count(path => path.Contains("/scripts/", StringComparison.Ordinal)));
        Assert.Equal(3,
            fixture.Handler.Calls.Count(path => path.Contains("/transactions/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RetryReusesProvenOwnedOutpoints()
    {
        using var fixture = new LookupFixture();
        var original = fixture.CreateOriginal(1, owned: true);
        var resolver = await fixture.ResolveAsync(original);
        Assert.NotNull(resolver);
        Assert.Equal(PayjoinInputOwnership.Owned, resolver.GetOwnership(original.InputOutpoints[0]));

        Assert.Null(await fixture.ResolveAsync(original));
        fixture.Clock.Advance(PayjoinOwnershipLookupStateStore.RetryDelay);
        resolver = await fixture.ResolveAsync(original);

        Assert.NotNull(resolver);
        Assert.Equal(PayjoinInputOwnership.Owned, resolver.GetOwnership(original.InputOutpoints[0]));
        Assert.Single(fixture.Handler.Calls, path => path.Contains("/transactions/", StringComparison.Ordinal));
        Assert.Single(fixture.Handler.Calls, path => path.Contains("/scripts/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PartialDependencyFailureStopsBeforeTheBudgetAndKeepsProvenOwnedInputs()
    {
        var logger = new RecordingLogger();
        using var fixture = new LookupFixture(logger: logger);
        var owned = fixture.CreateOriginal(2, owned: true);
        var foreign = fixture.CreateOriginal((PayjoinWalletOwnershipService.MaxRequestsPerPass - 1) / 2);
        var original = owned with { InputOutpoints = [.. owned.InputOutpoints, .. foreign.InputOutpoints] };
        var failedTransaction = foreign.InputOutpoints[0].Hash.ToString();
        fixture.Handler.RespondAsync = (request, token) => request.RequestUri!.AbsolutePath.Contains(failedTransaction, StringComparison.Ordinal)
            ? throw new HttpRequestException("Temporary funding lookup failure.")
            : fixture.DefaultResponseAsync(request, token);

        Assert.Equal(PayjoinOwnershipLookupStatus.Deferred, (await fixture.LookupAsync(original)).Status);
        Assert.Equal(5, fixture.Handler.Calls.Count);
        Assert.Single(logger.Entries, entry => entry.EventId.Id == 1);
        Assert.DoesNotContain(logger.Entries, entry => entry.EventId.Id == 2);
        fixture.Handler.RespondAsync = fixture.DefaultResponseAsync;
        Assert.Null(await fixture.ResolveAsync(original));
        Assert.Equal(5, fixture.Handler.Calls.Count);
        fixture.Clock.Advance(PayjoinOwnershipLookupStateStore.RetryDelay);

        var resolver = await fixture.ResolveAsync(original);

        Assert.NotNull(resolver);
        Assert.All(owned.InputOutpoints, input => Assert.Equal(PayjoinInputOwnership.Owned, resolver.GetOwnership(input)));
        Assert.All(foreign.InputOutpoints, input => Assert.Equal(PayjoinInputOwnership.Foreign, resolver.GetOwnership(input)));
        Assert.Equal(5 + 1 + 2 * foreign.InputOutpoints.Count, fixture.Handler.Calls.Count);
        foreach (var input in owned.InputOutpoints)
            Assert.Single(fixture.Handler.Calls, path => path.Contains(input.Hash.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task ShutdownCancellationPropagatesAndReleasesItsSlotWithoutResettingBackoff()
    {
        var logger = new RecordingLogger();
        using var fixture = new LookupFixture(logger: logger);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.RespondAsync = async (request, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return await fixture.DefaultResponseAsync(request, token).ConfigureAwait(false);
        };
        var original = fixture.CreateOriginal(1);
        var resolving = fixture.Service.CreateResolverAsync(fixture.Session, original, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(PayjoinOwnershipLookupStatus.Deferred, (await fixture.LookupAsync(original)).Status);
        Assert.Single(fixture.Handler.Calls);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolving);
        fixture.Handler.RespondAsync = fixture.DefaultResponseAsync;

        Assert.Equal(PayjoinOwnershipLookupStatus.Deferred, (await fixture.LookupAsync(original)).Status);
        Assert.Single(fixture.Handler.Calls);
        Assert.NotNull(await fixture.ResolveAsync(original, fixture.CreateSession("another")));
        fixture.Clock.Advance(PayjoinOwnershipLookupStateStore.RetryDelay);
        Assert.NotNull(await fixture.ResolveAsync(original));
        var requests = fixture.Handler.Calls.Count;
        fixture.Clock.Advance(PayjoinOwnershipLookupStateStore.RetryDelay);
        Assert.Equal(PayjoinOwnershipLookupStatus.Deferred, (await fixture.LookupAsync(original)).Status);
        Assert.Equal(requests, fixture.Handler.Calls.Count);
        fixture.Clock.Advance(PayjoinOwnershipLookupStateStore.RetryDelay);
        Assert.NotNull(await fixture.ResolveAsync(original));
        Assert.Equal(0, fixture.Handler.Active);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task BlockedLookupsReleaseReceiverWorkersForAnotherSession()
    {
        using var fixture = new LookupFixture(lookupTimeout: TimeSpan.FromMilliseconds(300));
        using var database = new SessionStoreFixture();
        var store = database.CreateStore();
        var blockedCount = PayjoinReceiverSessionProcessor.MaxConcurrentReceiverSessions;
        for (var index = 0; index < blockedCount; index++)
            store.GetOrCreateSession($"blocked-{index}", string.Empty, "store", DateTimeOffset.UtcNow.AddMinutes(10), ["bootstrap"]);
        store.GetOrCreateSession("healthy", string.Empty, "store", DateTimeOffset.UtcNow.AddMinutes(10), ["bootstrap"]);
        Assert.Equal(blockedCount + 1, store.GetSessions().Count);
        var blocked = fixture.CreateOriginal(1);
        var healthy = fixture.CreateOriginal(1);
        fixture.Handler.RespondAsync = async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains(blocked.InputOutpoints[0].Hash.ToString(), StringComparison.Ordinal))
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return await fixture.DefaultResponseAsync(request, token).ConfigureAwait(false);
        };
        var guard = new OwnershipGuard(fixture.Service, blocked, healthy);
        var processor = new PayjoinReceiverSessionProcessor(store, guard, null!, null!, null!,
            database.CreateBridgeService(), null!, null!, NullLogger<PayjoinReceiverSessionProcessor>.Instance);
        var maxTicks = (blockedCount + PayjoinOwnershipLookupStateStore.MaxConcurrentPasses - 1)
            / PayjoinOwnershipLookupStateStore.MaxConcurrentPasses + 1;
        for (var tick = 0; tick < maxTicks && !guard.HealthyCompleted; tick++)
            await processor.ProcessTickAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(guard.HealthyCompleted);
        Assert.Equal(PayjoinOwnershipLookupStateStore.MaxConcurrentPasses, fixture.Handler.MaxActive);
        Assert.Equal(0, fixture.Handler.Active);
        Assert.Equal(blockedCount + 1, store.GetSessions().Count);
    }

    [Fact]
    public async Task ReplacementSessionAndWalletConfigurationHaveSeparateBudgets()
    {
        using var fixture = new LookupFixture();
        fixture.Handler.RespondAsync = (_, _) => throw new HttpRequestException("Unavailable.");
        var original = fixture.CreateOriginal(1);
        Assert.Null(await fixture.ResolveAsync(original));
        fixture.Handler.RespondAsync = fixture.DefaultResponseAsync;
        Assert.Null(await fixture.ResolveAsync(original));
        var replacement = fixture.CreateSession(fixture.Session.InvoiceId);
        Assert.NotNull(await fixture.ResolveAsync(original, replacement));
        Assert.NotEqual(fixture.Session.SessionInstanceId, replacement.SessionInstanceId);
        fixture.Wallet = fixture.Wallet with { AccountDerivation = fixture.OtherDerivation };
        Assert.NotNull(await fixture.ResolveAsync(original));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullCacheDoesNotEvictAndResetBackoffOrAnExhaustedBudget(bool budgetExceeded)
    {
        var logger = new RecordingLogger();
        using var fixture = new LookupFixture(logger: logger);
        var original = fixture.CreateOriginal(budgetExceeded ? PayjoinWalletOwnershipService.MaxRequestsPerPass / 2 : 1);
        if (!budgetExceeded) fixture.Handler.RespondAsync = (_, _) => throw new HttpRequestException("Unavailable.");
        var status = budgetExceeded ? PayjoinOwnershipLookupStatus.Exhausted : PayjoinOwnershipLookupStatus.Deferred;
        Assert.Equal(status, (await fixture.LookupAsync(original)).Status);
        fixture.Handler.RespondAsync = fixture.DefaultResponseAsync;
        for (var index = 1; index < PayjoinOwnershipLookupStateStore.MaxCachedProposals; index++)
            Assert.NotNull(await fixture.ResolveAsync(fixture.CreateOriginal(0), fixture.CreateSession($"other-{index}")));
        var requests = fixture.Handler.Calls.Count;

        Assert.Equal(PayjoinOwnershipLookupStatus.CapacityUnavailable, (await fixture.LookupAsync(original, fixture.CreateSession("over-capacity"))).Status);
        Assert.Equal(PayjoinOwnershipLookupStatus.CapacityUnavailable, (await fixture.LookupAsync(original, fixture.CreateSession("another-over-capacity"))).Status);
        Assert.Equal(status, (await fixture.LookupAsync(original)).Status);
        Assert.Equal(requests, fixture.Handler.Calls.Count);
        Assert.Single(logger.Entries, entry => entry.EventId.Id == 3);
        fixture.Clock.Advance(PayjoinOwnershipLookupStateStore.RetryDelay);
        if (budgetExceeded)
        {
            Assert.Equal(PayjoinOwnershipLookupStatus.Exhausted, (await fixture.LookupAsync(original)).Status);
            Assert.Equal(requests, fixture.Handler.Calls.Count);
            Assert.Single(logger.Entries, entry => entry.EventId.Id == 2);
        }
        else
        {
            Assert.NotNull(await fixture.ResolveAsync(original));
            Assert.Equal(requests + 3, fixture.Handler.Calls.Count);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdvancingAttemptVersionDoesNotResetBackoffOrTheGenerationBudget(bool budgetExceeded)
    {
        using var fixture = new LookupFixture();
        using var database = new SessionStoreFixture();
        var store = database.CreateStore(fixture.States);
        fixture.IsSessionCurrent = store.IsCurrentSession;
        var session = store.GetOrCreateSession("versioned", "receiver", "store", fixture.Clock.GetUtcNow().AddHours(1), ["bootstrap"]);
        var original = fixture.CreateOriginal(budgetExceeded ? PayjoinWalletOwnershipService.MaxRequestsPerPass / 2 : 1);
        Assert.Equal(budgetExceeded ? PayjoinOwnershipLookupStatus.Exhausted : PayjoinOwnershipLookupStatus.Ready,
            (await fixture.LookupAsync(original, session)).Status);
        var requests = fixture.Handler.Calls.Count;
        using (var context = database.CreateDbContext())
        {
            context.ReceiverSessions.Single(row => row.InvoiceId == session.InvoiceId).AttemptVersion++;
            context.SaveChanges();
        }
        Assert.True(store.TryGetSession(session.InvoiceId, out var advanced));
        Assert.NotNull(advanced);
        Assert.Equal(session.SessionInstanceId, advanced.SessionInstanceId);
        Assert.Equal(session.AttemptVersion + 1, advanced.AttemptVersion);

        Assert.Equal(budgetExceeded ? PayjoinOwnershipLookupStatus.Exhausted : PayjoinOwnershipLookupStatus.Deferred,
            (await fixture.LookupAsync(original, advanced)).Status);
        Assert.Equal(requests, fixture.Handler.Calls.Count);
        if (!budgetExceeded)
        {
            fixture.Clock.Advance(PayjoinOwnershipLookupStateStore.RetryDelay);
            Assert.NotNull(await fixture.ResolveAsync(original, advanced));
        }
    }

    [Fact]
    public async Task FullPassesRemainRetryableAfterTheThirdFailure()
    {
        using var fixture = new LookupFixture();
        var budget = PayjoinWalletOwnershipService.MaxRequestsPerPass;
        var original = fixture.CreateOriginal((budget - 1) / 2);
        fixture.MissingFunding.Add(original.InputOutpoints[0].Hash);
        const int failedPasses = 4;
        fixture.Handler.RespondAsync = (request, token) => fixture.Handler.Calls.Count % budget == 0
            && fixture.Handler.Calls.Count / budget <= failedPasses
                ? throw new HttpRequestException("Temporary final-operation failure.")
                : fixture.DefaultResponseAsync(request, token);
        for (var pass = 0; pass < failedPasses; pass++)
        {
            Assert.Equal(PayjoinOwnershipLookupStatus.Deferred, (await fixture.LookupAsync(original)).Status);
            Assert.Equal((pass + 1) * budget, fixture.Handler.Calls.Count);
            fixture.Clock.Advance(TimeSpan.FromSeconds(ExpectedBackoffSeconds[pass]));
        }

        Assert.Equal(PayjoinOwnershipLookupStatus.Ready, (await fixture.LookupAsync(original)).Status);
        Assert.Equal((failedPasses + 1) * budget, fixture.Handler.Calls.Count);
        Assert.Equal(PayjoinOwnershipLookupStatus.Deferred, (await fixture.LookupAsync(original)).Status);
        Assert.Equal((failedPasses + 1) * budget, fixture.Handler.Calls.Count);
        fixture.Clock.Advance(PayjoinOwnershipLookupStateStore.MaxRetryDelay);
        Assert.Equal(PayjoinOwnershipLookupStatus.Ready, (await fixture.LookupAsync(original)).Status);
        Assert.Equal((failedPasses + 2) * budget, fixture.Handler.Calls.Count);
    }

    [Fact]
    public async Task SuccessfulPassesHaveCooldownWithoutExhaustionOrTickLogging()
    {
        var logger = new RecordingLogger();
        using var fixture = new LookupFixture(logger: logger);
        var original = fixture.CreateOriginal(1);
        foreach (var delaySeconds in ExpectedBackoffSeconds)
        {
            Assert.Equal(PayjoinOwnershipLookupStatus.Ready, (await fixture.LookupAsync(original)).Status);
            var requests = fixture.Handler.Calls.Count;
            var loaded = new TaskCompletionSource<PayjoinWalletOwnershipService.WalletContext?>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.LoadWallet = () => loaded.Task;
            var concurrent = Enumerable.Range(0, PayjoinReceiverSessionProcessor.MaxConcurrentReceiverSessions)
                .Select(_ => fixture.LookupAsync(original)).ToArray();
            loaded.SetResult(fixture.Wallet);
            Assert.All(await Task.WhenAll(concurrent), result => Assert.Equal(PayjoinOwnershipLookupStatus.Deferred, result.Status));
            fixture.LoadWallet = null;
            fixture.Clock.Advance(TimeSpan.FromSeconds(delaySeconds) - TimeSpan.FromMilliseconds(1));
            Assert.Equal(PayjoinOwnershipLookupStatus.Deferred, (await fixture.LookupAsync(original)).Status);
            Assert.Equal(requests, fixture.Handler.Calls.Count);
            fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
        }
        Assert.NotNull(await fixture.ResolveAsync(original));
        Assert.Equal((ExpectedBackoffSeconds.Length + 1) * 3, fixture.Handler.Calls.Count);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task HealthyConcurrentProposalsCompleteInTwoAdmissionRounds()
    {
        using var fixture = new LookupFixture();
        var original = fixture.CreateOriginal(1);
        var sessions = Enumerable.Range(0, 2 * PayjoinOwnershipLookupStateStore.MaxConcurrentPasses)
            .Select(index => fixture.CreateSession($"healthy-{index}")).ToArray();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        fixture.Handler.RespondAsync = async (request, token) =>
        {
            if (Interlocked.Increment(ref started) == PayjoinOwnershipLookupStateStore.MaxConcurrentPasses) allStarted.TrySetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            return await fixture.DefaultResponseAsync(request, token).ConfigureAwait(false);
        };
        var first = sessions.Select(session => fixture.LookupAsync(original, session)).ToArray();
        await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(PayjoinOwnershipLookupStateStore.MaxConcurrentPasses, first.Count(task => task.IsCompletedSuccessfully && task.Result.Status == PayjoinOwnershipLookupStatus.Deferred));
        Assert.Equal(PayjoinOwnershipLookupStateStore.MaxConcurrentPasses, fixture.Handler.Active);
        release.TrySetResult();
        var results = await Task.WhenAll(first);
        Assert.Equal(PayjoinOwnershipLookupStateStore.MaxConcurrentPasses, results.Count(result => result.Status == PayjoinOwnershipLookupStatus.Ready));
        for (var index = 0; index < sessions.Length; index++)
            if (results[index].Status == PayjoinOwnershipLookupStatus.Deferred)
                Assert.Equal(PayjoinOwnershipLookupStatus.Ready, (await fixture.LookupAsync(original, sessions[index])).Status);

        Assert.Equal(sessions.Length * 3, fixture.Handler.Calls.Count);
        Assert.Equal(PayjoinOwnershipLookupStateStore.MaxConcurrentPasses, fixture.Handler.MaxActive);
    }

    [Fact]
    public async Task BusySlotsDoNotStartOrIncreaseBackoff()
    {
        using var fixture = new LookupFixture();
        var original = fixture.CreateOriginal(1);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        fixture.Handler.RespondAsync = async (request, token) =>
        {
            if (Interlocked.Increment(ref calls) == PayjoinOwnershipLookupStateStore.MaxConcurrentPasses) started.TrySetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            return await fixture.DefaultResponseAsync(request, token).ConfigureAwait(false);
        };
        var running = Enumerable.Range(0, PayjoinOwnershipLookupStateStore.MaxConcurrentPasses)
            .Select(index => fixture.LookupAsync(original, fixture.CreateSession($"running-{index}"))).ToArray();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var waiting = fixture.CreateSession("waiting");
        for (var tick = 0; tick < 10; tick++)
            Assert.Equal(PayjoinOwnershipLookupStatus.Deferred, (await fixture.LookupAsync(original, waiting)).Status);
        Assert.Equal(PayjoinOwnershipLookupStateStore.MaxConcurrentPasses, fixture.Handler.Calls.Count);
        release.TrySetResult();
        Assert.All(await Task.WhenAll(running), result => Assert.Equal(PayjoinOwnershipLookupStatus.Ready, result.Status));
        Assert.NotNull(await fixture.ResolveAsync(original, waiting));
        var requests = fixture.Handler.Calls.Count;
        Assert.Null(await fixture.ResolveAsync(original, waiting));
        Assert.Equal(requests, fixture.Handler.Calls.Count);
        fixture.Clock.Advance(PayjoinOwnershipLookupStateStore.RetryDelay);
        Assert.NotNull(await fixture.ResolveAsync(original, waiting));
        Assert.Equal(requests + 3, fixture.Handler.Calls.Count);
        Assert.Equal(0, fixture.Handler.Active);
    }

    [Fact]
    public async Task RetryingOldProposalsStillAdmitALaterHealthyInvoiceInTheSameStore()
    {
        using var fixture = new LookupFixture(lookupTimeout: TimeSpan.FromMilliseconds(300));
        using var database = new SessionStoreFixture();
        var store = database.CreateStore(fixture.States);
        var blockedCount = PayjoinReceiverSessionProcessor.MaxConcurrentReceiverSessions;
        for (var index = 0; index < blockedCount; index++)
            store.GetOrCreateSession($"blocked-{index}", string.Empty, "store", fixture.Clock.GetUtcNow().AddHours(1), ["bootstrap"]);
        var blocked = fixture.CreateOriginal(1);
        var healthy = fixture.CreateOriginal(1);
        var blockedTransaction = blocked.InputOutpoints[0].Hash.ToString();
        fixture.Handler.RespondAsync = async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains(blockedTransaction, StringComparison.Ordinal))
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return await fixture.DefaultResponseAsync(request, token).ConfigureAwait(false);
        };
        var guard = new OwnershipGuard(fixture.Service, blocked, healthy);
        var processor = new PayjoinReceiverSessionProcessor(store, guard, null!, null!, null!,
            database.CreateBridgeService(), null!, null!, NullLogger<PayjoinReceiverSessionProcessor>.Instance);
        var rounds = (blockedCount + PayjoinOwnershipLookupStateStore.MaxConcurrentPasses - 1)
            / PayjoinOwnershipLookupStateStore.MaxConcurrentPasses;
        const int retryCycles = 4;
        for (var cycle = 0; cycle < retryCycles; cycle++)
        {
            for (var tick = 0; tick < rounds; tick++)
                await processor.ProcessTickAsync(TestContext.Current.CancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal((cycle + 1) * blockedCount,
                fixture.Handler.Calls.Count(path => path.Contains(blockedTransaction, StringComparison.Ordinal)));
            fixture.Clock.Advance(PayjoinOwnershipLookupStateStore.MaxRetryDelay);
        }
        store.GetOrCreateSession("healthy", string.Empty, "store", fixture.Clock.GetUtcNow().AddHours(1), ["bootstrap"]);
        for (var tick = 0; tick <= rounds && !guard.HealthyCompleted; tick++)
        {
            await processor.ProcessTickAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.True(guard.HealthyCompleted);
        Assert.Equal(PayjoinOwnershipLookupStateStore.MaxConcurrentPasses, fixture.Handler.MaxActive);
        Assert.Equal(0, fixture.Handler.Active);
        Assert.Equal(blockedCount + 1, store.GetSessions().Count);
    }

    [Fact]
    public async Task SessionExpiryStopsRetriesWithoutExtendingItsDeadline()
    {
        using var fixture = new LookupFixture();
        var original = fixture.CreateOriginal(1);
        fixture.Handler.RespondAsync = (_, _) => Task.FromResult(ServiceUnavailable());
        for (var pass = 0; pass < 4; pass++)
        {
            Assert.Equal(PayjoinOwnershipLookupStatus.Deferred, (await fixture.LookupAsync(original)).Status);
            fixture.Clock.Advance(TimeSpan.FromSeconds(ExpectedBackoffSeconds[pass]));
        }
        var requests = fixture.Handler.Calls.Count;
        fixture.Clock.Advance(fixture.Session.MonitoringExpiresAt - fixture.Clock.GetUtcNow());
        fixture.Handler.RespondAsync = fixture.DefaultResponseAsync;

        Assert.Equal(PayjoinOwnershipLookupStatus.Exhausted, (await fixture.LookupAsync(original)).Status);
        Assert.Equal(requests, fixture.Handler.Calls.Count);
        Assert.NotNull(await fixture.ResolveAsync(original, fixture.CreateSession(fixture.Session.InvoiceId)));
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
            .Select(index => store.GetOrCreateSession($"completed-{index}", "receiver", "store", fixture.Clock.GetUtcNow().AddHours(1), ["bootstrap"]))
            .ToArray();
        var original = fixture.CreateOriginal(0);
        foreach (var session in sessions) Assert.NotNull(await fixture.ResolveAsync(original, session));
        var other = fixture.CreateSession("new", "another-store");
        Assert.Equal(PayjoinOwnershipLookupStatus.CapacityUnavailable, (await fixture.LookupAsync(original, other)).Status);
        foreach (var session in sessions)
            Assert.True(discard ? store.TryRemoveSessionUnlessNegotiating(session.InvoiceId) : store.RemoveSession(session.InvoiceId));

        Assert.Empty(store.GetSessions());
        Assert.NotNull(await fixture.ResolveAsync(original, other));
    }

    [Fact]
    public async Task ExpiredEntriesReleaseCapacityBeforeAdmittingAnotherProposal()
    {
        using var fixture = new LookupFixture();
        var original = fixture.CreateOriginal(0);
        for (var index = 0; index < PayjoinOwnershipLookupStateStore.MaxCachedProposals; index++)
            Assert.NotNull(await fixture.ResolveAsync(original, fixture.CreateSession($"expired-{index}")));
        fixture.Clock.Advance(TimeSpan.FromHours(2));

        Assert.NotNull(await fixture.ResolveAsync(original, fixture.CreateSession("new")));
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
        fixture.LoadWallet = () => { walletLoads++; return Task.FromResult<PayjoinWalletOwnershipService.WalletContext?>(fixture.Wallet); };
        var original = fixture.CreateOriginal(1);
        var sessions = Enumerable.Range(0, PayjoinOwnershipLookupStateStore.MaxCachedProposals)
            .Select(index => store.GetOrCreateSession($"deleted-{index}", "receiver", "store", fixture.Clock.GetUtcNow().AddHours(1), ["bootstrap"]))
            .ToArray();
        foreach (var session in sessions)
        {
            Assert.True(discard ? store.TryRemoveSessionUnlessNegotiating(session.InvoiceId) : store.RemoveSession(session.InvoiceId));
            Assert.Equal(PayjoinOwnershipLookupStatus.Exhausted, (await fixture.LookupAsync(original, session)).Status);
        }
        Assert.Empty(store.GetSessions());
        Assert.Empty(fixture.Handler.Calls);
        Assert.Equal(0, walletLoads);
        var old = sessions[0];
        var replacement = store.GetOrCreateSession(old.InvoiceId, "receiver", "store", fixture.Clock.GetUtcNow().AddHours(1), ["bootstrap"]);
        Assert.NotEqual(old.SessionInstanceId, replacement.SessionInstanceId);
        Assert.NotNull(await fixture.ResolveAsync(original, replacement));
        Assert.Equal(PayjoinOwnershipLookupStatus.Exhausted, (await fixture.LookupAsync(original, old)).Status);
        foreach (var delaySeconds in ExpectedBackoffSeconds)
        {
            Assert.Null(await fixture.ResolveAsync(original, replacement));
            fixture.Clock.Advance(TimeSpan.FromSeconds(delaySeconds));
            Assert.NotNull(await fixture.ResolveAsync(original, replacement));
            Assert.Equal(PayjoinOwnershipLookupStatus.Exhausted, (await fixture.LookupAsync(original, old)).Status);
        }
        var other = store.GetOrCreateSession("healthy", "receiver", "another-store", fixture.Clock.GetUtcNow().AddHours(1), ["bootstrap"]);
        Assert.NotNull(await fixture.ResolveAsync(original, other));
        Assert.Equal(3 * (ExpectedBackoffSeconds.Length + 2), fixture.Handler.Calls.Count);
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

        Assert.Equal(PayjoinOwnershipLookupStatus.Exhausted, (await fixture.LookupAsync(fixture.CreateOriginal(1), session)).Status);
        Assert.Empty(fixture.Handler.Calls);
        Assert.Empty(store.GetSessions());
    }

    [Fact]
    public async Task DeletionDuringWalletLoadingFencesOnlyTheOldGeneration()
    {
        using var fixture = new LookupFixture();
        var original = fixture.CreateOriginal(1);
        var loaded = new TaskCompletionSource<PayjoinWalletOwnershipService.WalletContext?>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.LoadWallet = () => loaded.Task;
        var oldLookup = fixture.LookupAsync(original);
        fixture.States.Forget(PayjoinOwnershipSessionKey.From(fixture.Session));
        fixture.LoadWallet = null;
        var replacement = fixture.CreateSession(fixture.Session.InvoiceId);
        Assert.Equal(fixture.Session.CreatedAt, replacement.CreatedAt);
        Assert.NotEqual(fixture.Session.SessionInstanceId, replacement.SessionInstanceId);
        Assert.NotNull(await fixture.ResolveAsync(original, replacement));
        loaded.TrySetResult(fixture.Wallet);
        Assert.Equal(PayjoinOwnershipLookupStatus.Exhausted, (await oldLookup.ConfigureAwait(true)).Status);
        Assert.Equal(3, fixture.Handler.Calls.Count);
        fixture.States.Forget(PayjoinOwnershipSessionKey.From(fixture.Session));
        foreach (var delaySeconds in ExpectedBackoffSeconds)
        {
            Assert.Null(await fixture.ResolveAsync(original, replacement));
            fixture.Clock.Advance(TimeSpan.FromSeconds(delaySeconds));
            Assert.NotNull(await fixture.ResolveAsync(original, replacement));
        }
        Assert.Equal(3 * (ExpectedBackoffSeconds.Length + 1), fixture.Handler.Calls.Count);
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
        fixture.Handler.RespondAsync = async (request, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            return await fixture.DefaultResponseAsync(request, token).ConfigureAwait(false);
        };
        var original = fixture.CreateOriginal(1);
        var lookup = fixture.LookupAsync(original, session);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(store.RemoveSession(session.InvoiceId));
        release.TrySetResult();
        Assert.Equal(PayjoinOwnershipLookupStatus.Exhausted, (await lookup.ConfigureAwait(true)).Status);
        fixture.Handler.RespondAsync = fixture.DefaultResponseAsync;

        Assert.NotNull(await fixture.ResolveAsync(original, fixture.CreateSession("healthy")));
        Assert.Equal(0, fixture.Handler.Active);
    }

    private static HttpResponseMessage ServiceUnavailable() => new(HttpStatusCode.ServiceUnavailable)
    {
        Content = new StringContent("{\"httpCode\":503,\"code\":\"unavailable\",\"message\":\"Indexer is starting.\"}", Encoding.UTF8, "application/json")
    };

    internal sealed class LookupFixture : IDisposable
    {
        private readonly HttpClient _http;
        private readonly ExplorerClient _client;
        private readonly Script _foreignScript;
        private readonly Script _ownedScript;
        private readonly Dictionary<uint256, Transaction> _funding = [];
        private int _sequence;
        internal RecordingHandler Handler { get; } = new();
        internal ManualTimeProvider Clock { get; } = new();
        internal PayjoinWalletOwnershipService Service { get; }
        internal PayjoinOwnershipLookupStateStore States { get; }
        internal Func<Task<PayjoinWalletOwnershipService.WalletContext?>>? LoadWallet { get; set; }
        internal Func<PayjoinOwnershipSessionKey, bool> IsSessionCurrent { get; set; } = _ => true;
        internal PayjoinWalletOwnershipService.WalletContext Wallet { get; set; }
        internal DerivationStrategyBase OtherDerivation { get; }
        internal PayjoinReceiverSessionState Session { get; }
        internal HashSet<uint256> MissingFunding { get; } = [];
        internal bool UnconfirmedOnly { get; set; }

        internal LookupFixture(TimeSpan? lookupTimeout = null, ILogger<PayjoinWalletOwnershipService>? logger = null)
        {
            _http = new HttpClient(Handler) { Timeout = Timeout.InfiniteTimeSpan };
            var network = new NBXplorerNetworkProvider(ChainName.Regtest).GetBTC();
            _client = network.CreateExplorerClient(new Uri("http://nbxplorer.example/"));
            _client.SetClient(_http);
            _client.SetNoAuth();
            _client.RPCClient.HttpClient = _http;
            var hot = network.DerivationStrategyFactory.CreateDirectDerivationStrategy(new ExtKey().Neuter());
            OtherDerivation = network.DerivationStrategyFactory.CreateDirectDerivationStrategy(new ExtKey().Neuter());
            Wallet = new PayjoinWalletOwnershipService.WalletContext(_client, hot);
            _ownedScript = ((StandardDerivationStrategyBase)hot).GetDerivation(new KeyPath("0/0")).ScriptPubKey;
            using var foreign = new Key();
            _foreignScript = foreign.PubKey.WitHash.ScriptPubKey;
            States = new PayjoinOwnershipLookupStateStore(Clock);
            Service = new PayjoinWalletOwnershipService((_, _) => LoadWallet?.Invoke()
                ?? Task.FromResult<PayjoinWalletOwnershipService.WalletContext?>(Wallet), States,
                session => IsSessionCurrent(session), logger, lookupTimeout: lookupTimeout);
            Session = CreateSession("invoice");
            Handler.RespondAsync = DefaultResponseAsync;
        }

        internal PayjoinReceiverSessionState CreateSession(string invoiceId, string storeId = "store")
            => new(invoiceId, storeId, string.Empty, Clock.GetUtcNow().AddHours(1), Clock.GetUtcNow(), Clock.GetUtcNow(),
                sessionInstanceId: Guid.NewGuid());

        internal PayjoinOriginalTransactionFacts CreateOriginal(int distinctTransactions, bool sameScript = false,
            bool owned = false, int outputsPerFunding = 1)
        {
            var inputs = new List<OutPoint>();
            for (var index = 0; index < distinctTransactions; index++)
            {
                var funding = Network.RegTest.CreateTransaction();
                funding.Inputs.Add(new OutPoint(uint256.One, (uint)++_sequence));
                var scriptBytes = new byte[20];
                BitConverter.GetBytes(_sequence).CopyTo(scriptBytes, 0);
                var script = owned ? _ownedScript : sameScript ? _foreignScript : new WitKeyId(scriptBytes).ScriptPubKey;
                for (var output = 0; output < outputsPerFunding; output++)
                    funding.Outputs.Add(Money.Satoshis(1_000), script);
                var id = funding.GetHash();
                for (var output = 0; output < outputsPerFunding; output++) inputs.Add(new OutPoint(id, (uint)output));
                _funding.Add(id, funding);
            }
            return new PayjoinOriginalTransactionFacts(new uint256((ulong)++_sequence), inputs);
        }

        internal async Task<PayjoinInputOwnershipResolver?> ResolveAsync(PayjoinOriginalTransactionFacts original, PayjoinReceiverSessionState? session = null)
            => (await LookupAsync(original, session).ConfigureAwait(false)).Resolver;

        internal Task<PayjoinOwnershipLookupResult> LookupAsync(PayjoinOriginalTransactionFacts original, PayjoinReceiverSessionState? session = null)
            => Service.CreateResolverAsync(session ?? Session, original, TestContext.Current.CancellationToken);

        internal async Task<HttpResponseMessage> DefaultResponseAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            if (path == $"/v1/cryptos/BTC/derivations/{Wallet.AccountDerivation}")
                return new HttpResponseMessage(HttpStatusCode.OK);
            if (path.StartsWith("/v1/cryptos/BTC/transactions/", StringComparison.Ordinal))
            {
                var id = uint256.Parse(path[(path.LastIndexOf('/') + 1)..]);
                return !MissingFunding.Contains(id) && _funding.TryGetValue(id, out var funding)
                    ? JsonResponse(new TransactionResult { TransactionHash = id, Transaction = funding })
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            if (path.StartsWith("/v1/cryptos/BTC/scripts/", StringComparison.Ordinal))
            {
                KeyPathInformation[] records = path.EndsWith('/' + _ownedScript.ToHex(), StringComparison.Ordinal)
                    ? [new KeyPathInformation { DerivationStrategy = Wallet.AccountDerivation, ScriptPubKey = _ownedScript, KeyPath = new KeyPath("0/0") }]
                    : [];
                return JsonResponse(records);
            }
            if (path == "/v1/cryptos/BTC/rpc")
            {
                var rpc = JObject.Parse(await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                Assert.Equal("gettxout", rpc["method"]!.ToString());
                var parameters = (JArray)rpc["params"]!;
                var hash = uint256.Parse(parameters[0]!.ToString());
                var index = (uint)parameters[1]!;
                var includeMempool = (bool)parameters[2]!;
                var output = _funding.TryGetValue(hash, out var funding)
                    ? index < funding.Outputs.Count ? funding.Outputs[(int)index] : null
                    : new TxOut(Money.Satoshis(1_000), _foreignScript);
                if (UnconfirmedOnly && !includeMempool) output = null;
                return JsonResponse(new
                {
                    result = output is null ? null : new
                    {
                        bestblock = uint256.One.ToString(), confirmations = includeMempool ? 0 : 1,
                        value = output.Value.ToDecimal(MoneyUnit.BTC),
                        scriptPubKey = new { asm = output.ScriptPubKey.ToString(), hex = output.ScriptPubKey.ToHex(), type = "witness_v0_keyhash" },
                        coinbase = false
                    },
                    error = (object?)null, id = rpc["id"]
                });
            }
            throw new InvalidOperationException("Unexpected ownership request: " + path);
        }

        internal HttpResponseMessage JsonResponse(object? response) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonConvert.SerializeObject(response, _client.Network.Serializer.Settings), Encoding.UTF8, "application/json")
        };

        public void Dispose() => _http.Dispose();
    }

    internal sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly object _sync = new();
        internal List<string> Calls { get; } = [];
        internal int Active { get; private set; }
        internal int MaxActive { get; private set; }
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> RespondAsync { get; set; }
            = (_, _) => throw new InvalidOperationException("The fixture must configure its response handler.");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                Calls.Add(request.RequestUri!.AbsolutePath);
                Active++;
                MaxActive = Math.Max(MaxActive, Active);
            }
            try { return await RespondAsync(request, cancellationToken).ConfigureAwait(false); }
            finally { lock (_sync) Active--; }
        }
    }

    internal sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private sealed class RecordingLogger : ILogger<PayjoinWalletOwnershipService>
    {
        internal ConcurrentQueue<(LogLevel Level, EventId EventId)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Enqueue((level, eventId));
    }

    private sealed class OwnershipGuard(PayjoinWalletOwnershipService service, PayjoinOriginalTransactionFacts blocked,
        PayjoinOriginalTransactionFacts healthy) : IPayjoinReceiverSessionGuard
    {
        internal bool HealthyCompleted { get; private set; }
        public async Task<PayjoinReceiverSessionGuardResult?> TryPrepareAsync(PayjoinReceiverSessionState session, CancellationToken cancellationToken)
        {
            var result = await service.CreateResolverAsync(session, session.InvoiceId == "healthy" ? healthy : blocked, cancellationToken).ConfigureAwait(false);
            if (session.InvoiceId == "healthy") HealthyCompleted = result.Status == PayjoinOwnershipLookupStatus.Ready;
            return null;
        }
    }
}
