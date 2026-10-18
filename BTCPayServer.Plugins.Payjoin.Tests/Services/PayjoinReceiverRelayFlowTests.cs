using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Payjoin.Data;
using NBXplorer;
using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services.Wallets;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NSubstitute;
using Payjoin;
using System.Net;
using Xunit;
using OutPoint = NBitcoin.OutPoint;
using TxOut = NBitcoin.TxOut;
using Uri = System.Uri;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinReceiverRelayFlowTests
{
    [Theory]
    [InlineData(0, "MaybeInputsOwned")]
    [InlineData(1, "MaybeInputsSeen")]
    [InlineData(2, "OutputsUnknown")]
    public async Task OwnershipReplayRecoversAfterOnePersistenceFailure(int successfulSaves, string expectedState)
    {
        using var lookups = new PayjoinOwnershipLookupBudgetTests.LookupFixture();
        using var fixture = new ReceiverFixture(walletOwnership: lookups.Service, ownershipLookups: lookups.States);
        lookups.IsSessionCurrent = fixture.Store.IsCurrentSession;
        await fixture.PostOriginalAsync();
        var failureAfterSaves = successfulSaves;
        void FailSelectedSave()
        {
            if (failureAfterSaves-- == 0) fixture.Factory.FailSaveChanges = true;
            else fixture.Factory.BeforeNextSaveChanges = FailSelectedSave;
        }
        lookups.Handler.RespondAsync = (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/scripts/", StringComparison.Ordinal))
                fixture.Factory.BeforeNextSaveChanges = FailSelectedSave;
            return lookups.DefaultResponseAsync(request, token);
        };
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        var firstPassRequests = lookups.Handler.Calls.Count;
        Assert.True(firstPassRequests > 0);
        using (var replay = fixture.Replay())
        using (var state = replay.State()) Assert.Equal(expectedState, state.GetType().Name);
        fixture.Factory.FailSaveChanges = false;
        fixture.Factory.BeforeNextSaveChanges = null;
        lookups.Handler.RespondAsync = lookups.DefaultResponseAsync;
        var continued = false;
        var context = fixture.Context();
        using (var replay = fixture.Replay())
        using (var state = replay.State())
        {
            Task Continue(WantsOutputs proposal, PayjoinReceiverStateContext stateContext, CancellationToken token) { continued = true; return Task.CompletedTask; }
            switch (state)
            {
                case ReceiveSession.MaybeInputsOwned owned:
                    await fixture.StateProcessor.ProcessMaybeInputsOwnedAsync(context, owned.Inner, Continue, CancellationToken.None);
                    break;
                case ReceiveSession.MaybeInputsSeen seen:
                    await fixture.StateProcessor.ProcessMaybeInputsSeenAsync(context, seen.Inner, Continue, CancellationToken.None);
                    break;
                case ReceiveSession.OutputsUnknown outputs:
                    await fixture.StateProcessor.ProcessOutputsUnknownAsync(context, outputs.Inner, Continue, CancellationToken.None);
                    break;
            }
        }
        Assert.True(continued);
        Assert.Equal(successfulSaves == 0 ? 2 * firstPassRequests : firstPassRequests, lookups.Handler.Calls.Count);
        using var resumed = fixture.Replay();
        using var resumedState = resumed.State();
        Assert.IsType<ReceiveSession.WantsOutputs>(resumedState);
        Assert.Equal(0, fixture.ProposalSigner.Calls);
        for (var pass = 0; pass < PayjoinOwnershipLookupStateStore.MaxPasses; pass++)
            Assert.Equal(PayjoinOwnershipLookupStatus.Ready, (await lookups.Service.CreateResolverAsync(context.Session,
                context.OriginalTransaction!, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(PayjoinOwnershipLookupStatus.Exhausted, (await lookups.Service.CreateResolverAsync(context.Session,
            context.OriginalTransaction!, TestContext.Current.CancellationToken)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedSessionDeletionRetainsTheOwnershipBudget(bool discard)
    {
        using var lookups = new PayjoinOwnershipLookupBudgetTests.LookupFixture();
        using var fixture = new ReceiverFixture(walletOwnership: lookups.Service, ownershipLookups: lookups.States);
        var session = fixture.Session();
        var original = new PayjoinOriginalTransactionFacts(uint256.One, []);
        for (var pass = 0; pass < PayjoinOwnershipLookupStateStore.MaxPasses; pass++)
            Assert.NotNull(await lookups.ResolveAsync(original, session));
        fixture.Factory.FailSaveChanges = true;
        Assert.Throws<Microsoft.EntityFrameworkCore.DbUpdateException>(() =>
        {
            if (discard) fixture.Store.TryRemoveSessionUnlessNegotiating(session.InvoiceId, session.SessionInstanceId);
            else fixture.Store.RemoveSession(session.InvoiceId, session.SessionInstanceId);
        });
        fixture.Factory.FailSaveChanges = false;
        Assert.True(fixture.Store.TryGetSession(session.InvoiceId, out _));
        Assert.Equal(PayjoinOwnershipLookupStatus.Exhausted, (await lookups.LookupAsync(original, session)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplayedCompletedAndReplyableErrorStatesReleaseTheOwnershipBudget(bool replyableError)
    {
        using var lookups = new PayjoinOwnershipLookupBudgetTests.LookupFixture();
        using var fixture = new ReceiverFixture(walletOwnership: lookups.Service, ownershipLookups: lookups.States);
        if (replyableError) await fixture.PrepareReplyableErrorAsync();
        else { using var proposal = await fixture.PrepareProposalAsync(); }
        var session = fixture.Session();
        var original = new PayjoinOriginalTransactionFacts(uint256.One, []);
        for (var pass = 0; pass < PayjoinOwnershipLookupStateStore.MaxPasses; pass++)
            Assert.NotNull(await lookups.ResolveAsync(original, session));
        using var replay = fixture.Replay();
        using var state = replay.State();
        if (replyableError) Assert.IsType<ReceiveSession.HasReplyableError>(state);
        else Assert.IsType<ReceiveSession.PayjoinProposal>(state);
        fixture.Store.CompleteOwnershipLookupIfFinished(session, state);
        fixture.Store.CompleteOwnershipLookupIfFinished(session, state);
        Assert.NotNull(await lookups.ResolveAsync(original, session));
    }

    [Fact]
    public async Task DeferredOwnershipLookupRetainsNativeStateAndResumesWithoutRecordingInputsEarly()
    {
        using var lookups = new PayjoinOwnershipLookupBudgetTests.LookupFixture(lookupTimeout: TimeSpan.FromMilliseconds(100));
        using var fixture = new ReceiverFixture(walletOwnership: lookups.Service, ownershipLookups: lookups.States);
        await fixture.PostOriginalAsync();
        lookups.Handler.RespondAsync = async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/scripts/", StringComparison.Ordinal))
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return await lookups.DefaultResponseAsync(request, token).ConfigureAwait(false);
        };
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        var deferredEvents = fixture.Events();
        var deferredRequests = lookups.Handler.Calls.Count;
        using (var replay = fixture.Replay())
        using (var state = replay.State())
            Assert.IsType<ReceiveSession.MaybeInputsOwned>(state);
        using (var database = fixture.Factory.CreateContext()) Assert.Empty(database.ReceiverSeenInputs);

        await fixture.Processor.ProcessTickAsync(CancellationToken.None);

        Assert.Equal(deferredEvents, fixture.Events());
        Assert.Equal(deferredRequests, lookups.Handler.Calls.Count);
        Assert.Equal(0, fixture.ProposalSigner.Calls);
        lookups.Clock.Advance(PayjoinOwnershipLookupStateStore.RetryDelay);
        lookups.Handler.RespondAsync = lookups.DefaultResponseAsync;
        var continued = false;
        using (var replay = fixture.Replay())
        using (var state = replay.State())
        {
            var owned = Assert.IsType<ReceiveSession.MaybeInputsOwned>(state);
            await fixture.StateProcessor.ProcessMaybeInputsOwnedAsync(fixture.Context(), owned.Inner,
                (_, _, _) => { continued = true; return Task.CompletedTask; }, CancellationToken.None);
        }
        Assert.True(continued);
        using var resumed = fixture.Replay();
        using var resumedState = resumed.State();
        Assert.IsType<ReceiveSession.WantsOutputs>(resumedState);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OutputFailureCompletesOnlyTheProvenUnsignedAttempt(bool substitutionError, bool replay)
    {
        using var fixture = new ReceiverFixture();
        if (replay)
        {
            using var prepared = await fixture.PrepareProposalAsync();
            fixture.RestoreState("WantsOutputs");
        }
        else await fixture.PostOriginalAsync();
        var old = fixture.Session();
        fixture.Store.StorePayjoinUri("invoice", old.ReceiverAddress, "bitcoin:exact-cached-uri");
        PayjoinAccountingBridgeState? attached = null;
        fixture.Outputs.CheckPin = _ => attached = fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None).GetAwaiter().GetResult();
        fixture.Outputs.Result = OutputFailure(substitutionError);
        var responses = fixture.Transport.Responses.Count;
        var failedRelay = new Uri("https://failed-relay.example/");
        fixture.Manager.MarkRelayFailedForSession("invoice", failedRelay);
        Assert.Null(fixture.Manager.ChooseRelayForRequest([failedRelay], "invoice"));
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        Assert.NotNull(attached);
        Assert.NotNull(attached.FallbackTransactionId);
        Assert.Equal(50_000, attached.FallbackValueSats);
        Assert.Equal(50_000, attached.EffectiveInvoiceValueSats);
        Assert.Equal(1, fixture.Outputs.Calls);
        Assert.Equal(0, fixture.Inputs.Calls);
        Assert.Equal(0, fixture.ProposalSigner.Calls);
        Assert.Equal(responses + (replay ? 0 : 1), fixture.Transport.Responses.Count);
        Assert.False(fixture.Store.TryGetSession("invoice", out _));
        Assert.Equal(failedRelay, fixture.Manager.ChooseRelayForRequest([failedRelay], "invoice"));
        using var db = fixture.Factory.CreateContext();
        Assert.Empty(db.ReceiverSessions);
        Assert.Empty(db.ReceiverSessionEvents);
        Assert.Empty(db.AccountingBridges);
        Assert.Empty(db.ReceiverInputReservations);
        Assert.NotEmpty(db.ReceiverSeenInputs);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task OutputFailureKeepsConcurrentProgressOrAnUncommittedDeletion(bool substitutionError, bool storageFailure)
    {
        using var fixture = new ReceiverFixture();
        using var prepared = await fixture.PrepareProposalAsync();
        fixture.RestoreState("WantsOutputs");
        fixture.Store.StorePayjoinUri("invoice", fixture.Session().ReceiverAddress, "bitcoin:exact-retained-uri");
        PayjoinReceiverSessionState? winner = null;
        PayjoinAccountingBridgeState? bridge = null;
        fixture.Outputs.CheckPin = _ =>
        {
            if (storageFailure) fixture.Factory.FailSaveChanges = true;
            else
            {
                var persister = fixture.Store.CreatePersister(fixture.Session());
                using var replay = PayjoinMethods.ReplayReceiverEventLog(persister);
                using var state = replay.State();
                using var transition = Assert.IsType<ReceiveSession.WantsOutputs>(state).Inner.CommitOutputs();
                var capture = new CapturingReceiverSessionPersister();
                using var inputs = transition.Save(capture);
                fixture.Store.CommitReceiverOutputs(persister, capture.Events, new PayjoinReceiverOutputBuilder.OutputReplacement([], Convert.FromHexString(Convert.ToHexString(fixture.Context().ReceiverScript)), 50_000, null));
            }
            winner = fixture.Session();
            bridge = fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None).GetAwaiter().GetResult();
        };
        fixture.Outputs.Result = OutputFailure(substitutionError);
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        fixture.Factory.FailSaveChanges = false;
        Assert.NotNull(winner);
        Assert.Equal(winner.GetEvents(), fixture.Events());
        Assert.Equal(winner.PayjoinUri, fixture.Session().PayjoinUri);
        Assert.Equal(bridge, await fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None));
        Assert.Equal(0, fixture.Inputs.Calls);
        Assert.Equal(0, fixture.ProposalSigner.Calls);
        using var db = fixture.Factory.CreateContext();
        Assert.Empty(db.ReceiverInputReservations);
        Assert.NotEmpty(db.ReceiverSeenInputs);
    }

    private static PayjoinReceiverOutputBuilder.OutputBuildResult OutputFailure(bool substitutionError) => substitutionError
        ? new(PayjoinReceiverOutputBuilder.OutputBuildStatus.Ready, new PayjoinReceiverOutputBuilder.OutputReplacement([], [0x51], 50_000, null))
        : new(PayjoinReceiverOutputBuilder.OutputBuildStatus.InvalidDestination);

    [Fact]
    public async Task SnapshotObservationReplaysTheReadJournalWhenPostingAdvancesToMonitor()
    {
        using var fixture = new ReceiverFixture();
        using var prepared = await fixture.PrepareProposalAsync();
        await SeedCommittedBridgeAsync(fixture);
        var read = fixture.Session();
        var bound = fixture.Store.CreatePersister(read);
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        using var stale = Assert.Throws<ReceiverReplayException>(() => PayjoinMethods.ReplayReceiverEventLog(bound));
        Assert.True(PayjoinSessionStaleException.IsStale(stale));
        using (var replay = PayjoinMethods.ReplayReceiverEventLog(PayjoinReceiverSessionStore.Snapshot(read)))
        using (var state = replay.State()) Assert.IsType<ReceiveSession.PayjoinProposal>(state);
        using var fresh = PayjoinMethods.ReplayReceiverEventLog(PayjoinReceiverSessionStore.Snapshot(fixture.Session()));
        using var monitor = fresh.State();
        Assert.IsType<ReceiveSession.Monitor>(monitor);
        Assert.Equal(read.GetEvents().Length + 1, fixture.Events().Length);
    }

    [Theory]
    [InlineData(40_000UL)]
    [InlineData(60_000UL)]
    public async Task ProvenEarlyAmountRejectionAllowsNewAttemptButKeepsSeenInputs(ulong pin)
    {
        using var fixture = new ReceiverFixture(pin: pin);
        var old = fixture.Session();
        using (var db = fixture.Factory.CreateContext())
        {
            db.AccountingBridges.Single().EffectiveInvoiceValueSats = checked((long)pin);
            db.SaveChanges();
        }
        var stalePersister = fixture.Store.CreatePersister(old);
        await fixture.PostOriginalAsync();
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        Assert.False(fixture.Store.TryGetSession("invoice", out _));
        Assert.Null(await fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None));
        Assert.Throws<PayjoinSessionStaleException>(() => stalePersister.Load());
        using (var db = fixture.Factory.CreateContext())
        {
            Assert.Empty(db.ReceiverInputReservations);
            Assert.Empty(db.ReceiverSessionEvents);
            Assert.NotEmpty(db.ReceiverSeenInputs);
        }
        using var keys = fixture.Services.FetchOhttpKeys();
        using var builder = new ReceiverBuilder(old.ReceiverAddress, fixture.Services.DirectoryUrl(), keys);
        using var pinned = builder.WithAmount(50_000);
        using var transition = pinned.Build();
        var bootstrap = new CapturingReceiverSessionPersister();
        using var initialized = transition.Save(bootstrap);
        using var uri = initialized.PjUri();
        var next = fixture.Store.GetOrCreateSession("invoice", old.ReceiverAddress, "store", old.MonitoringExpiresAt,
            bootstrap.Events, new CreatePayjoinAccountingBridgeRequest("invoice", "store", "BTC", "BTC-CHAIN", old.MonitoringExpiresAt));
        Assert.NotEqual(old.SessionInstanceId, next.SessionInstanceId);
        await fixture.PostOriginalAsync(uri.AsString());
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        Assert.Equal(0, fixture.Outputs.Calls);
        Assert.Equal(0, fixture.Inputs.Calls);
        Assert.Equal(0, fixture.ProposalSigner.Calls);
        using var replay = fixture.Replay();
        using var state = replay.State();
        Assert.IsType<ReceiveSession.HasReplyableError>(state);
        var bridge = await fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None);
        Assert.Equal(PayjoinAccountingBridgeStatus.PendingFallback, bridge!.Status);
        Assert.Equal(50_000, bridge.EffectiveInvoiceValueSats);
    }

    [Fact]
    public async Task CommittedOutputsWithoutAvailableReceiverInputEndTheSessionWithoutRepeatedSelection()
    {
        using var fixture = new ReceiverFixture();
        using var proposal = await fixture.PrepareProposalAsync(replaceOutputs: true);
        fixture.RestoreState("WantsInputs");
        using (var replay = fixture.Replay())
        using (var history = replay.SessionHistory())
        {
            var original = Transaction.Load(history.FallbackTx()!, Network.RegTest);
            var output = original.Outputs.Select((value, index) => (value, index)).Single(x => x.value.Value.Satoshi == 50_000);
            await fixture.RecordFallbackAsync(original.GetHash().ToString(), output.index, 50_000, 50_000, null, CancellationToken.None);
        }
        fixture.Factory.SeedBridgeFields("invoice", bridge => bridge.SettlementScript = "51");
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        Assert.Equal(1, fixture.Inputs.Calls);
        Assert.Equal(0, fixture.ProposalSigner.Calls);
        Assert.False(fixture.Store.TryGetSession("invoice", out _));
        Assert.Null(await fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None));
        using (var db = fixture.Factory.CreateContext())
        {
            Assert.Empty(db.ReceiverSessionEvents);
            Assert.Empty(db.ReceiverInputReservations);
            Assert.NotEmpty(db.ReceiverSeenInputs);
        }
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        Assert.Equal(1, fixture.Inputs.Calls);
    }

    [Theory]
    [InlineData("signed", false)]
    [InlineData("expected-output", false)]
    [InlineData("failure", false)]
    [InlineData("pin", false)]
    [InlineData("original", false)]
    [InlineData("reservation", false)]
    [InlineData("expected-output", true)]
    [InlineData("failure", true)]
    [InlineData("pin", true)]
    [InlineData("original", true)]
    [InlineData("reservation", true)]
    [InlineData("settlement-script", true)]
    [InlineData("settlement-key", true)]
    [InlineData("contributed", true)]
    [InlineData("failed", true)]
    [InlineData("reconciled", true)]
    public async Task UnsignedCompletionKeepsPreparedOrInconsistentEvidence(string condition, bool beforeOutputs)
    {
        using var fixture = new ReceiverFixture();
        using var proposal = await fixture.PrepareProposalAsync(replaceOutputs: true);
        if (condition != "signed") fixture.RestoreState(beforeOutputs ? "WantsOutputs" : "WantsInputs");
        using (var replay = fixture.Replay())
        using (var history = replay.SessionHistory())
        {
            var tx = Transaction.Load(history.FallbackTx()!, Network.RegTest);
            var match = PayjoinReceiverPaymentValidation.ResolveFallbackReceiverOutput(tx, fixture.Context().ReceiverScript);
            await fixture.RecordFallbackAsync(tx.GetHash().ToString(), match.OutputIndex!.Value, 50_000, 50_000, beforeOutputs ? null : "51", CancellationToken.None);
        }
        if (condition == "reservation")
            Assert.True(fixture.Store.TryReserveContributedInput(fixture.Store.Owner("invoice"), "store", new OutPoint(uint256.One, 0), DateTimeOffset.UtcNow.AddMinutes(10)));
        else
        {
            using var db = fixture.Factory.CreateContext();
            var bridge = db.AccountingBridges.Single();
            switch (condition)
            {
                case "expected-output": bridge.ExpectedFinalOutputIndex = 0; break;
                case "failure": bridge.FailureMessage = "unresolved problem"; break;
                case "pin": bridge.EffectiveInvoiceValueSats = 60_000; break;
                case "original": bridge.FallbackValueSats = 40_000; break;
                case "settlement-script": bridge.SettlementScript = "51"; break;
                case "settlement-key": bridge.SettlementKeyPath = "1/0"; break;
                case "contributed": db.ReceiverSessions.Single().ContributedInputTransactionId = uint256.One.ToString(); break;
                case "failed": bridge.Status = PayjoinAccountingBridgeStatus.Failed; break;
                case "reconciled": bridge.ReconciledAt = DateTimeOffset.UtcNow; break;
            }
            db.SaveChanges();
        }
        var snapshot = fixture.Session();
        var bridgeBefore = await fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None);
        Assert.False(fixture.Store.TryCompleteUnsignedAttempt("invoice", snapshot.SessionInstanceId, snapshot.AttemptVersion, fixture.Context().ReceiverScript));
        Assert.Equal(snapshot.GetEvents(), fixture.Session().GetEvents());
        Assert.Equal(snapshot.PayjoinUri, fixture.Session().PayjoinUri);
        Assert.Equal(bridgeBefore, await fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None));
    }

    [Fact]
    public async Task SameInstanceRejectionPreservesSessionAndUriUntilAnotherRelayIsAvailable()
    {
        using var fixture = new ReceiverFixture();
        var rejectedRelay = new Uri(fixture.Services.DirectoryUrl());
        fixture.UseRelays(rejectedRelay);
        using var originalUri = fixture.Receiver.PjUri();

        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        Assert.True(fixture.Store.TryGetSession("invoice", out _));
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        var rejection = Assert.Single(fixture.Transport.Responses);
        Assert.Equal(HttpStatusCode.Forbidden, rejection.Status);
        Assert.Equal(0, rejection.Size);
        Assert.Equal(rejectedRelay, fixture.Manager.ChooseRelayForRequest([rejectedRelay], "another-invoice"));

        fixture.UseRelays(rejectedRelay, fixture.Transport.HealthyRelay);
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);

        using var replay = fixture.Replay();
        using var state = replay.State();
        var initialized = Assert.IsType<ReceiveSession.Initialized>(state);
        using var retainedUri = initialized.Inner.PjUri();
        Assert.Equal(originalUri.AsString(), retainedUri.AsString());
        Assert.Equal(2, fixture.Transport.Responses.Count);
        Assert.Equal((HttpStatusCode.OK, PayjoinReceiverRelayClient.MaxResponseBodySize), fixture.Transport.Responses[1]);
    }

    [Fact]
    public async Task TruncatedPollResponsePreservesSessionForNextTick()
    {
        using var fixture = new ReceiverFixture(TimeSpan.Zero);
        fixture.Transport.ResponseBody = [];

        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        Assert.True(fixture.Store.TryGetSession("invoice", out _));
        fixture.Transport.ResponseBody = null;
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);

        using var replay = fixture.Replay();
        using var state = replay.State();
        Assert.IsType<ReceiveSession.Initialized>(state);
        Assert.Equal(2, fixture.Transport.Responses.Count);
        Assert.Equal((HttpStatusCode.OK, PayjoinReceiverRelayClient.MaxResponseBodySize), fixture.Transport.Responses[1]);
    }

    [Fact]
    public async Task SuccessfulPollKeepsWantsOutputsAliveAcrossAsyncContinuation()
    {
        using var fixture = new ReceiverFixture();
        await fixture.PostOriginalAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var testCancellation = global::Xunit.TestContext.Current.CancellationToken;
        Task? continuation = null;

        var processing = fixture.StateProcessor.ProcessInitializedAsync(fixture.Context(), fixture.Receiver, (proposal, _, cancellationToken) =>
        {
            continuation = ContinueAsync(proposal, cancellationToken);
            entered.SetResult();
            return continuation;
        }, testCancellation);

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), testCancellation);
            Assert.False(processing.IsCompleted);
        }
        finally
        {
            resume.TrySetResult();
            await Task.WhenAll(processing, continuation ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5), testCancellation);
        }

        Assert.NotNull(continuation);
        using var replay = fixture.Replay();
        using var state = replay.State();
        Assert.IsType<ReceiveSession.WantsOutputs>(state);
        Assert.Equal(fixture.Transport.HealthyRelay, fixture.Manager.ChooseRelayForRequest([fixture.Transport.HealthyRelay], "invoice"));

        async Task ContinueAsync(WantsOutputs proposal, CancellationToken cancellationToken)
        {
            await resume.Task.WaitAsync(cancellationToken).ConfigureAwait(true);
            Assert.True(proposal.ProposalTxidIsStable());
        }
    }

    [Fact]
    public async Task WalletFailureAfterOriginalDoesNotQuarantineSuccessfulRelay()
    {
        using var fixture = new ReceiverFixture();
        await fixture.PostOriginalAsync();
        fixture.Wallet.FailInputLookup = true;

        await fixture.Processor.ProcessTickAsync(CancellationToken.None);

        Assert.Equal(1, fixture.Wallet.InputLookupCalls);
        using var replay = fixture.Replay();
        using var state = replay.State();
        Assert.IsType<ReceiveSession.MaybeInputsOwned>(state);
        Assert.Equal(fixture.Transport.HealthyRelay, fixture.Manager.ChooseRelayForRequest([fixture.Transport.HealthyRelay], "invoice"));
    }

    [Fact]
    public async Task UnknownInputOwnershipPreservesSessionAndReplaysBeforeNativeOwnershipCheck()
    {
        using var fixture = new ReceiverFixture();
        using var originalUri = fixture.Receiver.PjUri();
        fixture.Store.StorePayjoinUri("invoice", fixture.Session().ReceiverAddress, originalUri.AsString());
        fixture.Wallet.InputOwnership = PayjoinInputOwnership.Unknown;
        await fixture.PostOriginalAsync();

        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        var retained = fixture.Session();
        Assert.Equal(originalUri.AsString(), retained.PayjoinUri);
        var events = fixture.Events();
        var requests = fixture.Transport.Responses.Count;
        using (var replay = fixture.Replay())
        using (var state = replay.State())
        using (var history = replay.SessionHistory())
        using (var uri = history.PjUri())
        {
            Assert.IsType<ReceiveSession.MaybeInputsOwned>(state);
            Assert.Equal(originalUri.AsString(), uri.AsString());
        }

        await fixture.Processor.ProcessTickAsync(CancellationToken.None);

        Assert.Equal(events, fixture.Events());
        Assert.Equivalent(retained, fixture.Session(), strict: true);
        Assert.Equal(requests, fixture.Transport.Responses.Count);
        Assert.Equal(2, fixture.Wallet.InputLookupCalls);
        Assert.Equal(0, fixture.ProposalSigner.Calls);
        using (var database = fixture.Factory.CreateContext())
        {
            Assert.Empty(database.ReceiverSeenInputs);
            Assert.Empty(database.ReceiverInputReservations);
        }

        fixture.Wallet.InputOwnership = PayjoinInputOwnership.Owned;
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        Assert.True(fixture.Store.TryGetSession("invoice", out _));
        using var recoveredReplay = fixture.Replay();
        using var recoveredState = recoveredReplay.State();
        Assert.IsType<ReceiveSession.HasReplyableError>(recoveredState);
        Assert.Equal(3, fixture.Wallet.InputLookupCalls);
        Assert.Equal(0, fixture.ProposalSigner.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedInputOwnershipWaitsForEveryOutpoint(bool owned)
    {
        using var fixture = new ReceiverFixture();
        var knownOwnership = owned ? PayjoinInputOwnership.Owned : PayjoinInputOwnership.Foreign;
        fixture.Wallet.InputOwnershipLookup = input => input.N == 0 ? knownOwnership : PayjoinInputOwnership.Unknown;
        await fixture.PostOriginalAsync(inputCount: 2);

        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        var retained = fixture.Session();
        var events = fixture.Events();
        var requests = fixture.Transport.Responses.Count;
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);

        Assert.Equal(events, fixture.Events());
        Assert.Equivalent(retained, fixture.Session(), strict: true);
        Assert.Equal(requests, fixture.Transport.Responses.Count);
        using var replay = fixture.Replay();
        using var state = replay.State();
        Assert.IsType<ReceiveSession.MaybeInputsOwned>(state);
        Assert.Equal(2, fixture.Wallet.InputLookupCalls);
        Assert.Equal(0, fixture.ProposalSigner.Calls);
        using var database = fixture.Factory.CreateContext();
        Assert.Empty(database.ReceiverSeenInputs);
        Assert.Empty(database.ReceiverInputReservations);
    }

    [Fact]
    public async Task ForeignEvidenceResumesTheSameSessionAfterUnknown()
    {
        using var fixture = new ReceiverFixture();
        using var originalUri = fixture.Receiver.PjUri();
        fixture.Store.StorePayjoinUri("invoice", fixture.Session().ReceiverAddress, originalUri.AsString());
        fixture.Wallet.InputOwnership = PayjoinInputOwnership.Unknown;
        await fixture.PostOriginalAsync();
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        var events = fixture.Events();
        fixture.Wallet.InputOwnership = PayjoinInputOwnership.Foreign;
        var continued = false;

        using (var replay = fixture.Replay())
        using (var state = replay.State())
        {
            await fixture.StateProcessor.ProcessMaybeInputsOwnedAsync(fixture.Context(),
                Assert.IsType<ReceiveSession.MaybeInputsOwned>(state).Inner,
                (_, _, _) => { continued = true; return Task.CompletedTask; }, CancellationToken.None);
        }

        Assert.True(continued);
        Assert.Equal(originalUri.AsString(), fixture.Session().PayjoinUri);
        Assert.Equal(events, fixture.Events().Take(events.Length));
        using var resumedReplay = fixture.Replay();
        using var resumedState = resumedReplay.State();
        Assert.IsType<ReceiveSession.WantsOutputs>(resumedState);
        Assert.Equal(2, fixture.Wallet.InputLookupCalls);
        Assert.Equal(0, fixture.ProposalSigner.Calls);
        using var database = fixture.Factory.CreateContext();
        Assert.Single(database.ReceiverSeenInputs);
        Assert.Empty(database.ReceiverInputReservations);
    }

    [Fact]
    public async Task ExplorerRpcFailureAndUnknownPreserveSessionUntilOwnedEvidenceAppears()
    {
        using var explorer = new PayjoinWalletOwnershipServiceTests.OwnershipFixture();
        var ownedOutput = explorer.Funding!.Transaction.Outputs[1];
        explorer.RpcFailure = new HttpRequestException("injected Core RPC failure");
        using var fixture = new ReceiverFixture(walletOwnership: new ExplorerWallet(explorer));
        using var originalUri = fixture.Receiver.PjUri();
        fixture.Store.StorePayjoinUri("invoice", fixture.Session().ReceiverAddress, originalUri.AsString());
        await fixture.PostOriginalAsync();

        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        var retained = fixture.Session();
        var events = fixture.Events();
        var requests = fixture.Transport.Responses.Count;
        explorer.RpcFailure = null;
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);

        Assert.Equal(events, fixture.Events());
        Assert.Equivalent(retained, fixture.Session(), strict: true);
        Assert.Equal(requests, fixture.Transport.Responses.Count);
        using (var replay = fixture.Replay())
        using (var state = replay.State())
        {
            Assert.IsType<ReceiveSession.MaybeInputsOwned>(state);
        }
        Assert.Equal(3, explorer.FundingLookups);
        Assert.Equal(0, explorer.ScriptLookups);
        Assert.Equal(0, fixture.ProposalSigner.Calls);
        using (var database = fixture.Factory.CreateContext())
        {
            Assert.Empty(database.ReceiverSeenInputs);
            Assert.Empty(database.ReceiverInputReservations);
        }

        var input = new NBitcoin.OutPoint(uint256.Parse(new string('a', 64)), 0);
        explorer.UnspentOutputs[(input, false)] = ownedOutput;
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);

        Assert.Equal(originalUri.AsString(), fixture.Session().PayjoinUri);
        using var recoveredReplay = fixture.Replay();
        using var recoveredState = recoveredReplay.State();
        Assert.IsType<ReceiveSession.HasReplyableError>(recoveredState);
        Assert.Equal(4, explorer.FundingLookups);
        Assert.Equal(1, explorer.ScriptLookups);
        Assert.Equal(0, fixture.ProposalSigner.Calls);
    }

    [Fact]
    public async Task UnknownExplorerOwnershipReplaysFromARecreatedStore()
    {
        using var explorer = new PayjoinWalletOwnershipServiceTests.OwnershipFixture();
        using var fixture = new ReceiverFixture(walletOwnership: new ExplorerWallet(explorer));
        using var originalUri = fixture.Receiver.PjUri();
        fixture.Store.StorePayjoinUri("invoice", fixture.Session().ReceiverAddress, originalUri.AsString());
        await fixture.PostOriginalAsync();
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        var retained = fixture.Session();
        var events = fixture.Events();
        var restoredStore = new PayjoinReceiverSessionStore(fixture.Factory, new SqliteUniqueConstraintViolationDetector(), fixture.Manager);
        Assert.True(restoredStore.TryGetSession("invoice", out var restored));
        var session = Assert.IsType<PayjoinReceiverSessionState>(restored);
        var persister = restoredStore.CreatePersister(session);
        using var replay = PayjoinMethods.ReplayReceiverEventLog(persister);
        using var state = replay.State();
        var context = new PayjoinReceiverStateContext(session, persister, fixture.Context().ReceiverScript,
            "store", "invoice", _ => false);

        await fixture.StateProcessor.ProcessMaybeInputsOwnedAsync(context,
            Assert.IsType<ReceiveSession.MaybeInputsOwned>(state).Inner,
            static (_, _, _) => throw new InvalidOperationException("Unknown must retain the restored session."),
            TestContext.Current.CancellationToken);

        Assert.True(restoredStore.TryGetSession("invoice", out var afterRetry));
        Assert.Equivalent(retained, afterRetry, strict: true);
        Assert.Equal(events, fixture.Events());
        Assert.Equal(2, explorer.FundingLookups);
        Assert.Equal(0, fixture.ProposalSigner.Calls);
        using var database = fixture.Factory.CreateContext();
        Assert.Empty(database.ReceiverSeenInputs);
        Assert.Empty(database.ReceiverInputReservations);
    }

    [Fact]
    public async Task PollPersistenceFailureDoesNotQuarantineRelay()
    {
        using var fixture = new ReceiverFixture();
        await fixture.PostOriginalAsync();
        fixture.Factory.FailSaveChanges = true;

        using var error = await Assert.ThrowsAsync<ReceiverPersistedException.Storage>(() =>
            fixture.StateProcessor.ProcessInitializedAsync(fixture.Context(), fixture.Receiver,
                static (_, _, _) => throw new InvalidOperationException("Persistence must precede wallet processing."), CancellationToken.None));

        fixture.Factory.FailSaveChanges = false;
        Assert.Equal(fixture.Transport.HealthyRelay, fixture.Manager.ChooseRelayForRequest([fixture.Transport.HealthyRelay], "invoice"));
        using var replay = fixture.Replay();
        using var state = replay.State();
        Assert.IsType<ReceiveSession.Initialized>(state);
    }

    [Fact]
    public async Task FatalPollResponseRemainsFatalWithoutQuarantiningRelay()
    {
        using var fixture = new ReceiverFixture();
        fixture.Transport.ResponseBody = new byte[PayjoinReceiverRelayClient.MaxResponseBodySize];

        using var error = await Assert.ThrowsAsync<ReceiverPersistedException.Fatal>(() =>
            fixture.StateProcessor.ProcessInitializedAsync(fixture.Context(), fixture.Receiver,
                static (_, _, _) => throw new InvalidOperationException("Invalid ciphertext must not advance."), CancellationToken.None));

        Assert.Equal(fixture.Transport.HealthyRelay, fixture.Manager.ChooseRelayForRequest([fixture.Transport.HealthyRelay], "invoice"));
    }

    [Fact]
    public async Task TruncatedErrorReplyResponseQuarantinesRelayUntilRetryAfterExpiry()
    {
        var now = DateTimeOffset.UnixEpoch;
        var clock = Substitute.For<TimeProvider>();
        clock.GetUtcNow().Returns(_ => now);
        var quarantineDuration = TimeSpan.FromMinutes(10);
        using var fixture = new ReceiverFixture(quarantineDuration, clock);
        await fixture.PrepareReplyableErrorAsync();
        var eventsBeforeReply = fixture.Events();
        var responsesBeforeReply = fixture.Transport.Responses.Count;
        fixture.Transport.ResponseBody = [];

        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        Assert.Equal(responsesBeforeReply + 1, fixture.Transport.Responses.Count);
        Assert.Equal(eventsBeforeReply, fixture.Events());
        Assert.Null(fixture.Manager.ChooseRelayForRequest([fixture.Transport.HealthyRelay], "invoice"));
        Assert.Equal(fixture.Transport.HealthyRelay, fixture.Manager.ChooseRelayForRequest([fixture.Transport.HealthyRelay], "another-invoice"));
        using (var replay = fixture.Replay())
        using (var state = replay.State())
        {
            Assert.IsType<ReceiveSession.HasReplyableError>(state);
        }

        fixture.Transport.ResponseBody = null;
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        Assert.Equal(responsesBeforeReply + 1, fixture.Transport.Responses.Count);
        now += quarantineDuration - TimeSpan.FromTicks(1);
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        Assert.Equal(responsesBeforeReply + 1, fixture.Transport.Responses.Count);
        Assert.Equal(eventsBeforeReply, fixture.Events());

        now += TimeSpan.FromTicks(1);
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        Assert.Equal(responsesBeforeReply + 2, fixture.Transport.Responses.Count);
        using (var replay = fixture.Replay())
        using (var state = replay.State())
        {
            Assert.IsType<ReceiveSession.Closed>(state);
        }

        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        Assert.False(fixture.Store.TryGetSession("invoice", out _));
    }

    [Fact]
    public async Task RejectedPostRetriesPersistedProposalWithoutSigningAgain()
    {
        using var fixture = new ReceiverFixture();
        using var proposal = await fixture.PrepareProposalAsync();
        var expectedPsbt = proposal.Psbt();
        var eventsBeforePost = fixture.Events();
        fixture.UseRelays(new Uri(fixture.Services.DirectoryUrl()));
        await SeedCommittedBridgeAsync(fixture);

        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        Assert.True(fixture.Store.TryGetSession("invoice", out _));
        var recorded = await fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None);
        Assert.NotNull(recorded);
        Assert.Equal(PSBT.Parse(expectedPsbt, Network.RegTest).GetGlobalTransaction().GetHash().ToString(), recorded.ExpectedFinalTransactionId);
        Assert.Equal(50_000, recorded.FallbackValueSats);
        Assert.Equal(50_000, recorded.EffectiveInvoiceValueSats);
        Assert.Equal(eventsBeforePost, fixture.Events());
        using (var replay = fixture.Replay())
        using (var state = replay.State())
        {
            Assert.Equal(expectedPsbt, Assert.IsType<ReceiveSession.PayjoinProposal>(state).Inner.Psbt());
        }

        fixture.UseRelays(new Uri(fixture.Services.DirectoryUrl()), fixture.Transport.HealthyRelay);
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);

        using var completedReplay = fixture.Replay();
        using var completedState = completedReplay.State();
        Assert.IsType<ReceiveSession.Monitor>(completedState);
        Assert.Equal(0, fixture.ProposalSigner.Calls);
        Assert.Equal(eventsBeforePost.Length + 1, fixture.Events().Length);
    }

    [Fact]
    public async Task OversizedPostResponsePreservesProposalAndInputReservationForRetry()
    {
        var now = DateTimeOffset.UnixEpoch;
        var clock = Substitute.For<TimeProvider>();
        clock.GetUtcNow().Returns(_ => now);
        var quarantineDuration = TimeSpan.FromMinutes(10);
        using var fixture = new ReceiverFixture(quarantineDuration, clock);
        using var proposal = await fixture.PrepareProposalAsync();
        await SeedCommittedBridgeAsync(fixture);
        var expectedPsbt = proposal.Psbt();
        var eventsBeforePost = fixture.Events();
        var reservation = new PayjoinReceiverInputReservationData
        {
            InvoiceId = "invoice",
            StoreId = "store",
            TransactionId = new string('b', 64),
            OutputIndex = 0,
            ReservedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(20)
        };
        using (var context = fixture.Factory.CreateContext())
        {
            context.ReceiverInputReservations.Add(reservation);
            await context.SaveChangesAsync(global::Xunit.TestContext.Current.CancellationToken);
            await context.Entry(reservation).ReloadAsync(global::Xunit.TestContext.Current.CancellationToken);
        }
        fixture.Transport.ResponseBody = new byte[PayjoinReceiverRelayClient.MaxResponseBodySize + 1];
        var responsesBeforePost = fixture.Transport.Responses.Count;

        await fixture.Processor.ProcessTickAsync(CancellationToken.None);

        Assert.True(fixture.Store.TryGetSession("invoice", out _));
        Assert.Equal(eventsBeforePost, fixture.Events());
        Assert.Null(fixture.Manager.ChooseRelayForRequest([fixture.Transport.HealthyRelay], "invoice"));
        using (var replay = fixture.Replay())
        using (var state = replay.State())
        {
            Assert.Equal(expectedPsbt, Assert.IsType<ReceiveSession.PayjoinProposal>(state).Inner.Psbt());
        }
        using (var context = fixture.Factory.CreateContext())
        {
            var retained = Assert.Single(context.ReceiverInputReservations);
            Assert.Equal(reservation.Id, retained.Id);
            Assert.Equal(reservation.TransactionId, retained.TransactionId);
            Assert.Equal(reservation.OutputIndex, retained.OutputIndex);
            Assert.Equal(reservation.ExpiresAt, retained.ExpiresAt);
        }

        fixture.Transport.ResponseBody = null;
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        Assert.Equal(responsesBeforePost + 1, fixture.Transport.Responses.Count);
        now += quarantineDuration;
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);

        using var completedReplay = fixture.Replay();
        using var completedState = completedReplay.State();
        Assert.IsType<ReceiveSession.Monitor>(completedState);
        Assert.Equal(0, fixture.ProposalSigner.Calls);
        Assert.Equal(eventsBeforePost.Length + 1, fixture.Events().Length);
    }

    [Fact]
    public async Task PostPersistenceFailureDoesNotQuarantineRelay()
    {
        using var fixture = new ReceiverFixture();
        using var proposal = await fixture.PrepareProposalAsync();
        fixture.Factory.FailSaveChanges = true;

        using var error = await Assert.ThrowsAsync<ReceiverPersistedException.Storage>(() =>
            fixture.Finalizer.PostAsync(new PayjoinReceiverProposalFinalizationContext(fixture.Context().Persister, "store", "invoice", PayjoinConstants.BitcoinCode),
                proposal, CancellationToken.None));

        fixture.Factory.FailSaveChanges = false;
        Assert.Equal(fixture.Transport.HealthyRelay, fixture.Manager.ChooseRelayForRequest([fixture.Transport.HealthyRelay], "invoice"));
    }

    [Theory]
    [InlineData(40_000UL)]
    [InlineData(60_000UL)]
    [InlineData(0UL)]
    [InlineData(null)]
    public async Task CurrentTickRejectsOriginalBeforeBridgeOrOutputs(ulong? pin)
    {
        using var fixture = new ReceiverFixture(pin: pin);
        await fixture.PostOriginalAsync();
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        Assert.True(fixture.Store.TryGetSession("invoice", out _));
        Assert.Empty(fixture.Store.GetSessions());
        Assert.Equal(0, fixture.Outputs.Calls);
        Assert.Equal(0, fixture.Inputs.Calls);
        var bridge = await fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None);
        Assert.NotNull(bridge);
        Assert.Null(bridge.FallbackTransactionId);
        Assert.Null(bridge.FallbackValueSats);
        Assert.Equal(50_000, bridge.EffectiveInvoiceValueSats);
        Assert.Equal(PayjoinAccountingBridgeStatus.Failed, bridge.Status);
    }

    [Fact]
    public async Task CurrentTickAttachesValidatedHistoryBeforeBuildingOutputs()
    {
        using var fixture = new ReceiverFixture();
        string? observedOriginalId = null;
        long? observedOriginalAmount = null;
        long? observedBridgePin = null;
        long? observedBuilderPin = null;
        fixture.Outputs.CheckPin = pin =>
            {
                using var db = fixture.Factory.CreateContext();
                var bridge = db.AccountingBridges.Single();
                observedOriginalId = bridge.FallbackTransactionId;
                observedOriginalAmount = bridge.FallbackValueSats;
                observedBridgePin = bridge.EffectiveInvoiceValueSats;
                observedBuilderPin = pin;
            };
        await fixture.PostOriginalAsync();
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        Assert.NotNull(observedOriginalId);
        Assert.Equal(50_000, observedOriginalAmount);
        Assert.Equal(50_000, observedBridgePin);
        Assert.Equal(50_000, observedBuilderPin);
        Assert.Equal(1, fixture.Outputs.Calls);
        using var replay = fixture.Replay();
        using var state = replay.State();
        Assert.IsType<ReceiveSession.WantsOutputs>(state);
    }

    [Fact]
    public async Task WaitingForPayloadDoesNotRejectAnUnpinnedSession()
    {
        using var fixture = new ReceiverFixture(pin: null);
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        Assert.True(fixture.Store.TryGetSession("invoice", out _));
        var bridge = await fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None);
        Assert.Equal(PayjoinAccountingBridgeStatus.PendingFallback, bridge!.Status);
        Assert.Null(bridge.FailureMessage);
    }

    [Fact]
    public async Task FailureAfterTheGuardStillStopsTheCurrentPayloadBeforeOutputs()
    {
        using var fixture = new ReceiverFixture();
        await fixture.PostOriginalAsync();
        fixture.Wallet.BeforeInputLookup = () => fixture.BridgeService.MarkFailedAsync("invoice", "failure during payload processing", CancellationToken.None);

        await fixture.Processor.ProcessTickAsync(CancellationToken.None);

        Assert.Equal(0, fixture.Outputs.Calls);
        Assert.Equal(0, fixture.Inputs.Calls);
        Assert.Equal(0, fixture.ProposalSigner.Calls);
        Assert.True(fixture.Store.TryGetSession("invoice", out _));
        var bridge = await fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None);
        Assert.Equal(PayjoinAccountingBridgeStatus.Failed, bridge!.Status);
        Assert.Equal("failure during payload processing", bridge.FailureMessage);
        Assert.Null(bridge.FallbackTransactionId);
        using var replay = fixture.Replay();
        using var state = replay.State();
        // Failure changes the attempt version during input lookup, so its next native save is stale.
        Assert.IsType<ReceiveSession.MaybeInputsOwned>(state);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Expired")]
    [InlineData("Reconciled")]
    public async Task OnlyPendingBridgesPermitCurrentPayloadOutputs(string status)
    {
        using var fixture = new ReceiverFixture();
        await fixture.PostOriginalAsync();
        using (var db = fixture.Factory.CreateContext())
        {
            db.AccountingBridges.Single().Status = Enum.Parse<PayjoinAccountingBridgeStatus>(status);
            db.SaveChanges();
        }
        var before = await fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None);

        await fixture.Processor.ProcessTickAsync(CancellationToken.None);

        Assert.Equal(0, fixture.Outputs.Calls);
        Assert.Equal(0, fixture.Inputs.Calls);
        Assert.Equal(0, fixture.ProposalSigner.Calls);
        Assert.Equal(before, await fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None));
    }

    [Theory]
    [InlineData("WantsInputs")]
    [InlineData("WantsFeeRange")]
    [InlineData("ProvisionalProposal")]
    [InlineData("PayjoinProposal")]
    public async Task MatchingEventLogReplayPassesTheAmountGate(string phase)
    {
        using var fixture = new ReceiverFixture();
        using var proposal = await fixture.PrepareProposalAsync();
        fixture.RestoreState(phase);
        await SeedCommittedBridgeAsync(fixture);
        var requests = fixture.Transport.Responses.Count;

        await fixture.Processor.ProcessTickAsync(CancellationToken.None);

        var bridge = await fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None);
        if (phase == "WantsInputs")
        {
            Assert.Equal(1, fixture.Inputs.Calls);
            Assert.NotNull(fixture.Inputs.ReservationPersister);
            Assert.Null(bridge);
            Assert.False(fixture.Store.TryGetSession("invoice", out _));
        }
        else
        {
            Assert.NotNull(bridge);
            Assert.Equal(50_000, bridge.FallbackValueSats);
            Assert.Equal(50_000, bridge.EffectiveInvoiceValueSats);
            using var replay = fixture.Replay();
            using var state = replay.State();
            Assert.IsType<ReceiveSession.Monitor>(state);
            Assert.NotNull(bridge.ExpectedFinalTransactionId);
            Assert.Equal(requests + 1, fixture.Transport.Responses.Count);
            Assert.Equal(phase == "PayjoinProposal" ? 0 : 1, fixture.ProposalSigner.Calls);
        }
    }

    [Theory]
    [InlineData("WantsInputs", "script")]
    [InlineData("WantsFeeRange", "script")]
    [InlineData("ProvisionalProposal", "script")]
    [InlineData("WantsInputs", "expected-index")]
    [InlineData("WantsInputs", "expected-value")]
    public async Task ContradictoryCommittedSettlementStopsBeforeContributionAndSigning(string phase, string conflict)
    {
        using var fixture = new ReceiverFixture();
        using var prepared = await fixture.PrepareProposalAsync();
        fixture.RestoreState(phase);
        await SeedCommittedBridgeAsync(fixture);
        using (var db = fixture.Factory.CreateContext())
        {
            var bridge = db.AccountingBridges.Single();
            if (conflict == "script") bridge.SettlementScript = "51";
            if (conflict == "expected-index") bridge.ExpectedFinalOutputIndex = 0;
            if (conflict == "expected-value") bridge.ExpectedFinalValueSats = 50_000;
            db.SaveChanges();
        }
        var before = fixture.Session();
        var requests = fixture.Transport.Responses.Count;
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        Assert.Equal(before.GetEvents(), fixture.Events());
        Assert.Equal(before.PayjoinUri, fixture.Session().PayjoinUri);
        Assert.Equal(0, fixture.Inputs.Calls);
        Assert.Equal(0, fixture.ProposalSigner.Calls);
        Assert.Equal(requests, fixture.Transport.Responses.Count);
        Assert.Equal(PayjoinAccountingBridgeStatus.Failed,
            (await fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None))!.Status);
    }

    public static TheoryData<string, ulong?> RejectedReplayAmounts
    {
        get
        {
            var cases = new TheoryData<string, ulong?>();
            foreach (var state in new[] { "WantsInputs", "WantsFeeRange", "ProvisionalProposal", "PayjoinProposal" })
                foreach (var pin in new ulong?[] { null, 0, 40_000, 60_000 })
                    cases.Add(state, pin);
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(RejectedReplayAmounts))]
    public async Task ActualEventLogReplayRejectsBeforeContributionSigningOrPosting(string phase, ulong? pin)
    {
        using var fixture = new ReceiverFixture(pin: pin);
        using var proposal = await fixture.PrepareProposalAsync();
        fixture.RestoreState(phase);
        var requests = fixture.Transport.Responses.Count;
        var reservation = new OutPoint(uint256.One, 0);
        Assert.True(fixture.Store.TryReserveContributedInput(fixture.Store.Owner("invoice"), "store", reservation, DateTimeOffset.UtcNow.AddMinutes(20)));

        await fixture.Processor.ProcessTickAsync(CancellationToken.None);

        Assert.True(fixture.Store.TryGetSession("invoice", out _));
        Assert.Empty(fixture.Store.GetSessions());
        Assert.Equal(0, fixture.Outputs.Calls);
        Assert.Equal(0, fixture.Inputs.Calls);
        Assert.Equal(0, fixture.ProposalSigner.Calls);
        Assert.Equal(requests, fixture.Transport.Responses.Count);
        using var db = fixture.Factory.CreateContext();
        Assert.Single(db.ReceiverInputReservations);
        var bridge = Assert.Single(db.AccountingBridges);
        Assert.Null(bridge.ExpectedFinalTransactionId);
        Assert.Null(bridge.FallbackValueSats);
        Assert.Equal(50_000, bridge.EffectiveInvoiceValueSats);
        Assert.Equal(PayjoinAccountingBridgeStatus.Failed, bridge.Status);
    }

    [Theory]
    [InlineData("original")]
    [InlineData("pin")]
    [InlineData("proposal")]
    public async Task ConflictingPreparedProposalRemainsIntact(string conflict)
    {
        using var fixture = new ReceiverFixture();
        using var proposal = await fixture.PrepareProposalAsync();
        using var historyReplay = fixture.Replay();
        using var history = historyReplay.SessionHistory();
        var original = Transaction.Load(history.FallbackTx()!, Network.RegTest);
        var match = PayjoinReceiverPaymentValidation.ResolveFallbackReceiverOutput(original, original.Outputs[0].ScriptPubKey.ToBytes());
        await fixture.RecordFallbackAsync(
            conflict == "original" ? uint256.One.ToString() : original.GetHash().ToString(),
            match.OutputIndex!.Value, match.ValueSats!.Value, conflict == "pin" ? 60_000 : 50_000, null, CancellationToken.None);
        await fixture.RecordExpectedAsync(uint256.One.ToString(), 1, 123_456, CancellationToken.None);
        var before = await fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None);
        var requests = fixture.Transport.Responses.Count;
        using var uri = history.PjUri();
        fixture.Store.StorePayjoinUri("invoice", fixture.Session().ReceiverAddress, uri.AsString());
        var events = fixture.Events();
        var cachedUri = fixture.Session().PayjoinUri;

        await fixture.Processor.ProcessTickAsync(CancellationToken.None);

        Assert.True(fixture.Store.TryGetSession("invoice", out var held));
        Assert.Equal(events, held!.GetEvents());
        Assert.Equal(cachedUri, held.PayjoinUri);
        Assert.False(fixture.Store.RemoveSession("invoice"));
        Assert.False(fixture.Store.TryRemoveSessionUnlessNegotiating("invoice"));
        Assert.Empty(fixture.Store.GetSessions());
        var after = await fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None);
        Assert.NotNull(after);
        Assert.Equal(before, after with { Status = before!.Status, FailureMessage = before.FailureMessage, UpdatedAt = before.UpdatedAt });
        Assert.Equal(requests, fixture.Transport.Responses.Count);
    }

    [Theory]
    [InlineData("WantsOutputs")]
    [InlineData("WantsInputs")]
    [InlineData("WantsFeeRange")]
    [InlineData("ProvisionalProposal")]
    [InlineData("PayjoinProposal")]
    public async Task FailedBridgeWithMatchingAmountNeverContinues(string phase)
    {
        using var fixture = new ReceiverFixture();
        using var proposal = await fixture.PrepareProposalAsync();
        fixture.RestoreState(phase);
        var events = fixture.Events();
        var requests = fixture.Transport.Responses.Count;
        await fixture.BridgeService.MarkFailedAsync("invoice", "held for manual recovery", CancellationToken.None);
        var before = await fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None);

        await fixture.Processor.ProcessTickAsync(CancellationToken.None);

        Assert.Equal(events, fixture.Events());
        Assert.Equal(0, fixture.Outputs.Calls);
        Assert.Equal(0, fixture.Inputs.Calls);
        Assert.Equal(0, fixture.ProposalSigner.Calls);
        Assert.Equal(requests, fixture.Transport.Responses.Count);
        Assert.Equal(before, await fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None));
    }

    [Fact]
    public async Task FailedProposalSurvivesExpiryRetryAndServiceRecreationWithRecoverableInput()
    {
        using var fixture = new ReceiverFixture();
        using var proposal = await fixture.PrepareProposalAsync(contributeReceiver: true);
        using var replay = fixture.Replay();
        using var history = replay.SessionHistory();
        using var uri = history.PjUri();
        fixture.Store.StorePayjoinUri("invoice", fixture.Session().ReceiverAddress, uri.AsString());
        var events = fixture.Events();
        var outpoint = new OutPoint(uint256.One, 0);
        Assert.True(fixture.Store.TryReserveContributedInput(fixture.Store.Owner("invoice"), "store", outpoint, DateTimeOffset.UtcNow.AddMinutes(1)));
        await fixture.RecordExpectedAsync(uint256.One.ToString(), 0, 123_456, CancellationToken.None);
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        var failed = await fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None);
        Assert.Equal(PayjoinAccountingBridgeStatus.Failed, failed!.Status);
        using (var expired = fixture.Factory.CreateContext())
        {
            expired.ReceiverSessions.Single().MonitoringExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            expired.SaveChanges();
        }
        var future = DateTimeOffset.UtcNow.AddDays(2);
        Assert.Equal(1, fixture.Store.CleanupExpiredInputReservations(future));
        Assert.Empty(await fixture.BridgeService.ExpirePendingAsync(future, CancellationToken.None));
        Assert.Null(await fixture.BridgeService.TryRetryAsync("invoice", "store", future, CancellationToken.None));

        var restartedStore = new PayjoinReceiverSessionStore(fixture.Factory, new SqliteUniqueConstraintViolationDetector());
        var restartedBridge = new PayjoinAccountingBridgeService(fixture.Factory, new PayjoinSessionBuildLock());
        Assert.Empty(restartedStore.GetSessions());
        Assert.True(restartedStore.TryGetSession("invoice", out var held));
        Assert.True(held!.MonitoringExpiresAt < DateTimeOffset.UtcNow);
        Assert.False(held!.TryGetContributedInput(out _));
        Assert.Equal(events, held.GetEvents());
        Assert.Equal(uri.AsString(), held.PayjoinUri);
        Assert.Null(restartedStore.GetServablePayjoinUri("invoice", held.ReceiverAddress));
        Assert.False(restartedStore.RemoveSession("invoice"));
        Assert.False(restartedStore.TryRemoveSessionUnlessNegotiating("invoice"));
        Assert.Null(await restartedBridge.TryRetryAsync("invoice", "store", future, CancellationToken.None));
        var checkout = new PayjoinUriSessionService(fixture.Networks, restartedStore, null!, null!, new PayjoinSessionBuildLock(),
            restartedBridge, null!, NullLogger<PayjoinUriSessionService>.Instance);
        var fallback = await checkout.BuildAsync("BTC", held.ReceiverAddress, 0.0005m, new PayjoinStoreSettings(), true,
            "invoice", "store", held.MonitoringExpiresAt, CancellationToken.None);
        Assert.Equal(PayjoinUriSessionService.ManualRecoveryRequiredReason, fallback.Reason);
        Assert.False(fallback.Retryable);
        Assert.DoesNotContain("pj=", fallback.PaymentUrl, StringComparison.OrdinalIgnoreCase);
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        Assert.Equal(failed, await restartedBridge.TryGetByInvoiceIdAsync("invoice", CancellationToken.None));
        using var restored = PayjoinMethods.ReplayReceiverEventLog(restartedStore.CreatePersister(held));
        using var state = restored.State();
        var recovered = PSBT.Parse(Assert.IsType<ReceiveSession.PayjoinProposal>(state).Inner.Psbt(), Network.RegTest);
        Assert.Contains(recovered.GetGlobalTransaction().Inputs, input => input.PrevOut == outpoint);
        using var db = fixture.Factory.CreateContext();
        Assert.Empty(db.ReceiverInputReservations);
    }

    private sealed class PrefixPersister(string[] events) : JsonReceiverSessionPersister
    {
        public string[] Load() => events;
        public void Save(string @event) => throw new NotSupportedException();
        public void Close() { }
    }

    [Fact]
    public async Task ExtraLoadCannotAuthorizeAStaleNativeTransitionAfterConcurrentProgress()
    {
        using var fixture = new ReceiverFixture();
        using var prepared = await fixture.PrepareProposalAsync();
        fixture.RestoreState("WantsOutputs");
        fixture.Store.StorePayjoinUri("invoice", fixture.Session().ReceiverAddress, "bitcoin:retained-exact-uri");
        var old = fixture.Store.CreatePersister(fixture.Session());
        var oldVersion = old.AttemptVersion;
        using var staleReplay = PayjoinMethods.ReplayReceiverEventLog(old);
        using var staleState = staleReplay.State();
        var winner = fixture.Store.CreatePersister(fixture.Session());
        using (var replay = PayjoinMethods.ReplayReceiverEventLog(winner))
        using (var state = replay.State())
        using (var transition = Assert.IsType<ReceiveSession.WantsOutputs>(state).Inner.CommitOutputs())
        {
            var capture = new CapturingReceiverSessionPersister();
            using var next = transition.Save(capture);
            fixture.Store.CommitReceiverOutputs(winner, capture.Events, new PayjoinReceiverOutputBuilder.OutputReplacement([], [0x51], 50_000, null));
        }
        var saved = fixture.Session();
        var bridge = await fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None);
        Assert.Throws<PayjoinSessionStaleException>(() => old.Load());
        Assert.Equal(oldVersion, old.AttemptVersion);
        using var replayError = Assert.Throws<ReceiverReplayException>(() => PayjoinMethods.ReplayReceiverEventLog(old));
        Assert.True(PayjoinSessionStaleException.IsStale(replayError));
        Assert.Equal(oldVersion, old.AttemptVersion);
        using var oldTransition = Assert.IsType<ReceiveSession.WantsOutputs>(staleState).Inner.CommitOutputs();
        using var error = Assert.Throws<ReceiverPersistedException.Storage>(() => oldTransition.Save(old));
        Assert.True(PayjoinSessionStaleException.IsStale(error));
        Assert.Equal(oldVersion, old.AttemptVersion);
        Assert.Equal(saved.GetEvents(), fixture.Events());
        Assert.Equal(saved.PayjoinUri, fixture.Session().PayjoinUri);
        Assert.Equal(bridge, await fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None));
        var relay = fixture.Transport.HealthyRelay;
        Assert.Equal(relay, fixture.Manager.ChooseRelayForRequest([relay], "invoice"));
        Assert.Equal(0, fixture.Inputs.Calls);
        Assert.Equal(0, fixture.ProposalSigner.Calls);
    }

    [Fact]
    public async Task StaleNativeSaveDuringProcessingPreservesWinningHistoryWithoutQuarantiningRelay()
    {
        using var fixture = new ReceiverFixture();
        using var prepared = await fixture.PrepareProposalAsync();
        fixture.RestoreState("MaybeInputsOwned");
        fixture.Store.StorePayjoinUri("invoice", fixture.Session().ReceiverAddress, "bitcoin:retained-exact-uri");
        var bridge = await fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None);
        string[]? winningEvents = null;
        fixture.Wallet.BeforeInputLookup = () =>
        {
            var winner = fixture.Store.CreatePersister(fixture.Session());
            using var replay = PayjoinMethods.ReplayReceiverEventLog(winner);
            using var state = replay.State();
            using var ownership = Assert.IsType<ReceiveSession.MaybeInputsOwned>(state).Inner.CheckInputsNotOwned(new ForeignInput());
            using var maybeInputsSeen = ownership.Save(winner);
            winningEvents = fixture.Events();
            return Task.CompletedTask;
        };
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        Assert.NotNull(winningEvents);
        Assert.Equal(winningEvents, fixture.Events());
        Assert.Equal("bitcoin:retained-exact-uri", fixture.Session().PayjoinUri);
        Assert.Equal(bridge, await fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None));
        var relay = fixture.Transport.HealthyRelay;
        Assert.Equal(relay, fixture.Manager.ChooseRelayForRequest([relay], "invoice"));
        Assert.Equal(0, fixture.Outputs.Calls);
        Assert.Equal(0, fixture.Inputs.Calls);
        Assert.Equal(0, fixture.ProposalSigner.Calls);
    }

    [Theory]
    [InlineData("WantsFeeRange", false, false)]
    [InlineData("WantsFeeRange", true, false)]
    [InlineData("ProvisionalProposal", false, false)]
    [InlineData("ProvisionalProposal", true, false)]
    [InlineData("WantsFeeRange", true, true)]
    [InlineData("ProvisionalProposal", true, true)]
    public async Task UnavailableContributedInputKeepsEvidenceUntilRecoveryOrExpiry(string phase, bool restart, bool expire)
    {
        using var fixture = new ReceiverFixture();
        using var prepared = await fixture.PrepareProposalAsync(contributeReceiver: true);
        fixture.RestoreState(phase);
        await SeedCommittedBridgeAsync(fixture);
        var outpoint = new OutPoint(uint256.One, 0);
        Assert.True(fixture.Store.TryReserveContributedInput(fixture.Store.Owner("invoice"), "store", outpoint, DateTimeOffset.UtcNow.AddMinutes(10)));
        using (var replay = fixture.Replay())
        using (var history = replay.SessionHistory())
        using (var uri = history.PjUri())
            fixture.Store.StorePayjoinUri("invoice", fixture.Session().ReceiverAddress, uri.AsString());
        var before = fixture.Session();
        var bridge = await fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None);
        var requests = fixture.Transport.Responses.Count;
        fixture.Inputs.PersistedCoins = null;
        var processor = restart ? fixture.RecreateProcessor() : fixture.Processor;

        await processor.ProcessTickAsync(CancellationToken.None);
        await processor.ProcessTickAsync(CancellationToken.None);

        var held = fixture.Session();
        Assert.Equal(before.SessionInstanceId, held.SessionInstanceId);
        Assert.Equal(before.AttemptVersion, held.AttemptVersion);
        Assert.Equal(before.PayjoinUri, held.PayjoinUri);
        Assert.Equal(before.GetEvents(), held.GetEvents());
        Assert.Equal(bridge, await fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None));
        Assert.Equal(new[] { outpoint, outpoint }, fixture.Inputs.PersistedLookups);
        Assert.Equal(0, fixture.Inputs.ContributionCalls);
        Assert.Equal(0, fixture.ProposalSigner.Calls);
        Assert.Equal(requests, fixture.Transport.Responses.Count);
        using (var db = fixture.Factory.CreateContext()) Assert.Single(db.ReceiverInputReservations);

        if (expire)
        {
            Assert.Equal(1, fixture.Store.CleanupExpiredInputReservations(DateTimeOffset.UtcNow.AddMinutes(11)));
            Assert.Equal(before.GetEvents(), fixture.Session().GetEvents());
            using (var db = fixture.Factory.CreateContext())
            {
                db.ReceiverSessions.Single().MonitoringExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
                db.SaveChanges();
            }
            var guard = new PayjoinReceiverSessionGuard(fixture.Store, fixture.Networks, null!, NullLogger<PayjoinReceiverSessionGuard>.Instance);
            Assert.True(guard.TryExpireSession(fixture.Session()));
            Assert.False(fixture.Store.TryGetSession("invoice", out _));
            Assert.Equal(bridge, await fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None));
            Assert.Equal(0, fixture.ProposalSigner.Calls);
            Assert.Equal(requests, fixture.Transport.Responses.Count);
            using var verified = fixture.Factory.CreateContext();
            Assert.Empty(verified.ReceiverInputReservations);
            return;
        }

        fixture.Inputs.PersistedCoins = [new ReceivedCoin { OutPoint = outpoint }];
        fixture.ProposalSigner.Signer = fixture.ContributedInputSigner;
        await (restart ? fixture.RecreateProcessor() : processor).ProcessTickAsync(CancellationToken.None);

        Assert.Equal(before.SessionInstanceId, fixture.Session().SessionInstanceId);
        Assert.Equal(outpoint, fixture.Inputs.PersistedLookups.Last());
        Assert.Equal(0, fixture.Inputs.ContributionCalls);
        Assert.Equal(1, fixture.ProposalSigner.Calls);
        Assert.Equal(requests + 1, fixture.Transport.Responses.Count);
        using var resumed = fixture.Replay();
        using var state = resumed.State();
        Assert.IsType<ReceiveSession.Monitor>(state);
        var armed = await fixture.BridgeService.TryGetByInvoiceIdAsync("invoice", CancellationToken.None);
        Assert.NotNull(armed!.ExpectedFinalTransactionId);
        Assert.Equal(50_000, armed.EffectiveInvoiceValueSats);
    }

    private static async Task SeedCommittedBridgeAsync(ReceiverFixture fixture)
    {
        using var replay = fixture.Replay();
        using var history = replay.SessionHistory();
        var tx = Transaction.Load(history.FallbackTx()!, Network.RegTest);
        var match = PayjoinReceiverPaymentValidation.ResolveFallbackReceiverOutput(tx, fixture.Context().ReceiverScript);
        await fixture.RecordFallbackAsync(tx.GetHash().ToString(), match.OutputIndex!.Value,
            match.ValueSats!.Value, 50_000, Convert.ToHexString(fixture.Context().ReceiverScript), CancellationToken.None).ConfigureAwait(false);
    }

    private sealed class RecordingOutputs : IPayjoinReceiverOutputBuilder
    {
        public int Calls { get; private set; }
        public Action<long?>? CheckPin { get; set; }
        public PayjoinReceiverOutputBuilder.OutputBuildResult Result { get; set; } = new(PayjoinReceiverOutputBuilder.OutputBuildStatus.RetryableFailure);
        public Task<PayjoinReceiverOutputBuilder.OutputBuildResult> TryCreateSettlementOutputsAsync(string storeId, string invoiceId, byte[] receiverScript, bool preserveReceiverScript, long? pinnedSettlementAmountSats, CancellationToken cancellationToken)
        {
            Calls++;
            CheckPin?.Invoke(pinnedSettlementAmountSats);
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingInputs : IPayjoinReceiverInputSelector
    {
        public int Calls { get; private set; }
        public int ContributionCalls { get; private set; }
        public ReceivedCoin[]? PersistedCoins { get; set; } = [];
        public List<OutPoint?> PersistedLookups { get; } = [];
        public JsonReceiverSessionPersister? ReservationPersister { get; private set; }
        public Task<ReceiverInputContributionResult> TryContributeInputsAsync(WantsInputs proposal, string storeId, DateTimeOffset reservationExpiresAt, PayjoinReceiverSessionStore.DatabaseBackedReceiverPersister owner, CancellationToken cancellationToken)
        {
            Calls++;
            ContributionCalls++;
            ReservationPersister = owner;
            return Task.FromResult(ReceiverInputContributionResult.Failure("test stopped after contribution gate"));
        }
        public Task<ReceivedCoin[]?> TryGetPersistedContributedCoinsAsync(PayjoinReceiverSessionState session, CancellationToken cancellationToken)
        {
            Calls++;
            PersistedLookups.Add(session.TryGetContributedInput(out var outpoint) ? outpoint : null);
            return Task.FromResult(PersistedCoins);
        }
    }

    private sealed class ReceiverFixture : IDisposable
    {
        private readonly Key _key = new();
        private readonly Key _receiverInputKey = new();
        private readonly PayjoinReceiverRelayRequestSender _sender;
        public ProcessPsbt ContributedInputSigner => new ReceiverInputSigner(_receiverInputKey);
        private readonly IPayjoinStoreSettingsRepository _settings = Substitute.For<IPayjoinStoreSettingsRepository>();
        private readonly HttpClient _http;
        public TestServices Services { get; } = TestServices.Initialize();
        public SqliteTestPayjoinPluginDbContextFactory Factory { get; } = new();
        public LocalMailroomHandler Transport { get; }
        public PayjoinMailroomManager Manager { get; }
        public PayjoinReceiverSessionStore Store { get; }
        public PayjoinReceiverStateProcessor StateProcessor { get; }
        public PayjoinReceiverSessionProcessor Processor { get; }
        public PayjoinReceiverProposalFinalizer Finalizer { get; }
        public Initialized Receiver { get; }
        public PayjoinAccountingBridgeService BridgeService { get; }
        public BTCPayNetworkProvider Networks { get; }
        public RecordingOutputs Outputs { get; } = new();
        public RecordingInputs Inputs { get; } = new();
        public FaultingWallet Wallet { get; } = new();
        public RecordingProposalSigner ProposalSigner { get; }
        private PassthroughSigner Signer { get; } = new();
        private BitcoinAddress Address => _key.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);

        public ReceiverFixture(TimeSpan? quarantineDuration = null, TimeProvider? timeProvider = null,
            ulong? pin = 50_000, IPayjoinWalletOwnershipService? walletOwnership = null,
            PayjoinOwnershipLookupStateStore? ownershipLookups = null)
        {
            Services.WaitForServicesReady();
            Transport = new LocalMailroomHandler(Services);
            _http = new HttpClient(Transport);
            Manager = new PayjoinMailroomManager(NullLogger<PayjoinMailroomManager>.Instance,
                quarantineDuration ?? TimeSpan.FromMinutes(10), (_, _, _, _) => throw new NotSupportedException(), timeProvider);
            Store = new PayjoinReceiverSessionStore(Factory, new SqliteUniqueConstraintViolationDetector(), Manager,
                ownershipLookups: ownershipLookups);
            using var keys = Services.FetchOhttpKeys();
            using var builder = new ReceiverBuilder(Address.ToString(), Services.DirectoryUrl(), keys);
            using var pinnedBuilder = pin.HasValue ? builder.WithAmount(pin.Value) : null;
            using var bootstrap = (pinnedBuilder ?? builder).Build();
            var capture = new CapturingReceiverSessionPersister();
            Receiver = bootstrap.Save(capture);
            Store.GetOrCreateSession("invoice", Address.ToString(), "store", DateTimeOffset.UtcNow.AddMinutes(20), capture.Events);
            UseRelays(Transport.HealthyRelay);
            var httpFactory = Substitute.For<IHttpClientFactory>();
            httpFactory.CreateClient(Arg.Any<string>()).Returns(_http);
            var sender = new PayjoinReceiverRelayRequestSender(_settings, Manager, new PayjoinReceiverRelayClient(httpFactory));
            _sender = sender;
            StateProcessor = new PayjoinReceiverStateProcessor(Store, sender, walletOwnership ?? Wallet,
                new PayjoinSeenInputStore(Factory), Manager);
            var bridge = new PayjoinAccountingBridgeService(Factory, new PayjoinSessionBuildLock());
            BridgeService = bridge;
            Factory.SeedBridgeAsync(new CreatePayjoinAccountingBridgeRequest("invoice", "store", "BTC",
                PaymentTypes.CHAIN.GetPaymentMethodId("BTC").ToString(), DateTimeOffset.UtcNow.AddMinutes(20),
                EffectiveInvoiceValueSats: 50_000), CancellationToken.None).GetAwaiter().GetResult();
            var nbx = new NBXplorerNetworkProvider(ChainName.Regtest);
            var network = new BTCPayNetwork { CryptoCode = "BTC", NBXplorerNetwork = nbx.GetFromCryptoCode("BTC") };
            var networks = new BTCPayNetworkProvider([network], nbx, new Logs());
            Networks = networks;
            ProposalSigner = new RecordingProposalSigner(Signer);
            Finalizer = new PayjoinReceiverProposalFinalizer(sender, ProposalSigner, bridge, Store, networks);
            Processor = new PayjoinReceiverSessionProcessor(Store, new ReplayingGuard(Store, Address.ScriptPubKey.ToBytes()),
                StateProcessor, Outputs, Inputs, bridge, Finalizer, networks, NullLogger<PayjoinReceiverSessionProcessor>.Instance);
        }

        public PayjoinReceiverSessionProcessor RecreateProcessor()
        {
            var store = new PayjoinReceiverSessionStore(Factory, new SqliteUniqueConstraintViolationDetector(), Manager);
            var bridge = new PayjoinAccountingBridgeService(Factory, new PayjoinSessionBuildLock());
            var stateProcessor = new PayjoinReceiverStateProcessor(store, _sender, Wallet, new PayjoinSeenInputStore(Factory), Manager);
            var finalizer = new PayjoinReceiverProposalFinalizer(_sender, ProposalSigner, bridge, store, Networks);
            return new PayjoinReceiverSessionProcessor(store, new ReplayingGuard(store, Address.ScriptPubKey.ToBytes()),
                stateProcessor, Outputs, Inputs, bridge, finalizer, Networks, NullLogger<PayjoinReceiverSessionProcessor>.Instance);
        }

        public Task RecordFallbackAsync(string transactionId, long index, long value, long pin, string? script, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var session = Session();
            Factory.SeedBridgeFields("invoice", bridge =>
            {
                bridge.FallbackTransactionId = transactionId;
                bridge.FallbackOutputIndex = index;
                bridge.FallbackValueSats = value;
                bridge.EffectiveInvoiceValueSats = pin;
                bridge.SettlementScript = script ?? bridge.SettlementScript;
                bridge.Status = PayjoinAccountingBridgeStatus.PendingFinalTransaction;
            });
            return Task.CompletedTask;
        }

        public Task RecordExpectedAsync(string transactionId, long index, long value, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var session = Session();
            Factory.SeedBridgeFields("invoice", bridge =>
            {
                bridge.ExpectedFinalTransactionId = transactionId;
                bridge.ExpectedFinalOutputIndex = index;
                bridge.ExpectedFinalValueSats = value;
                bridge.Status = PayjoinAccountingBridgeStatus.PendingFinalTransaction;
            });
            return Task.CompletedTask;
        }

        public void UseRelays(params Uri[] relays) => _settings.GetAsync("store").Returns(Task.FromResult<PayjoinStoreSettings?>(new PayjoinStoreSettings { OhttpRelayUrls = relays }));

        public PayjoinReceiverSessionState Session()
        {
            Assert.True(Store.TryGetSession("invoice", out var session));
            return Assert.IsType<PayjoinReceiverSessionState>(session);
        }

        public PayjoinReceiverStateContext Context()
        {
            var context = new PayjoinReceiverStateContext(Session(), Store.CreatePersister(Session()), Address.ScriptPubKey.ToBytes(), "store", "invoice", _ => false);
            using var replay = Replay();
            using var history = replay.SessionHistory();
            if (history.FallbackTx() is { Length: > 0 } bytes)
                context.OriginalTransaction = PayjoinReceiverStateProcessor.ExtractTransactionFacts(bytes);
            return context;
        }
        public ReplayResult Replay() => PayjoinMethods.ReplayReceiverEventLog(Store.CreatePersister(Session()));

        public string[] Events()
        {
            using var context = Factory.CreateContext();
            return context.ReceiverSessionEvents.Where(e => e.InvoiceId == "invoice")
                .OrderBy(e => e.Sequence).Select(e => e.Event).ToArray();
        }

        public async Task PostOriginalAsync(string? paymentUri = null, int inputCount = 1)
        {
            // Synthetic native SegWit coin: this exercises protocol state, not an on-chain payment.
            using var senderKey = new Key();
            var senderAddress = senderKey.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
            // Keep the original amount pinned at 50_000 while requiring both coins in the two-input case.
            var coins = Enumerable.Range(0, inputCount).Select(index =>
                new Coin(new OutPoint(uint256.Parse(new string('a', 64)), (uint)index), new TxOut(Money.Satoshis(100_000L / inputCount), senderAddress)));
            var psbt = Network.RegTest.CreateTransactionBuilder().AddCoins(coins).AddKeys(senderKey)
                .Send(Address, Money.Satoshis(50_000)).SetChange(senderAddress).SendFees(Money.Satoshis(1_000)).BuildPSBT(true).Finalize();
            Assert.Equal(inputCount, psbt.Inputs.Count);
            using var parsed = paymentUri is null ? null : global::Payjoin.Uri.Parse(paymentUri);
            using var uri = parsed?.CheckPjSupported() ?? Receiver.PjUri();
            using var senderUri = uri.SetAmountSats(50_000);
            using var builder = new SenderBuilder(psbt.ToBase64(), senderUri);
            using var transition = builder.BuildRecommended(1000);
            var persister = new SenderPersister();
            using var sender = transition.Save(persister);
            using var request = sender.CreateV2PostRequest(Services.OhttpRelayUrl());
            var body = await SendNativeRequestAsync(request.Request).ConfigureAwait(false);
            using var response = sender.ProcessResponse(body, request.OhttpCtx);
            using var polling = response.Save(persister);
        }

        private async Task<InitializedTransitionOutcome> ReceiveOriginalAsync()
        {
            await PostOriginalAsync().ConfigureAwait(false);
            using var request = Receiver.CreatePollRequest(Transport.HealthyRelay.AbsoluteUri);
            var body = await SendNativeRequestAsync(request.Request).ConfigureAwait(false);
            using var response = Receiver.ProcessResponse(body, request.ClientResponse);
            return response.Save(Context().Persister);
        }

        public async Task PrepareReplyableErrorAsync()
        {
            using var outcome = await ReceiveOriginalAsync().ConfigureAwait(false);
            var payload = Assert.IsType<InitializedTransitionOutcome.Progress>(outcome).Inner;
            using var rejection = payload.CheckBroadcastSuitability(null, new RejectBroadcast());
            using var error = Assert.Throws<ReceiverPersistedException.Storage>(() => rejection.Save(Context().Persister));
        }

        public async Task<PayjoinProposal> PrepareProposalAsync(bool contributeReceiver = false, bool replaceOutputs = false)
        {
            using var outcome = await ReceiveOriginalAsync().ConfigureAwait(false);
            var payload = Assert.IsType<InitializedTransitionOutcome.Progress>(outcome).Inner;
            var persister = Context().Persister;
            using var interactive = payload.AssumeInteractiveReceiver();
            using var owned = interactive.Save(persister);
            using var ownership = owned.CheckInputsNotOwned(new ForeignInput());
            using var seen = ownership.Save(persister);
            using var outputs = new PayjoinSeenInputStore(Factory).ExecuteSeenInputTransition(persister, (callback, writer) =>
            {
                using var history = seen.CheckNoInputsSeenBefore(callback);
                return history.Save(writer);
            });
            using var identify = outputs.IdentifyReceiverOutputs(new ReceiverOutput(Address.ScriptPubKey.ToBytes()));
            using var wantsOutputs = identify.Save(persister);
            using var replacement = replaceOutputs ? wantsOutputs.ReplaceReceiverOutputs([new global::Payjoin.TxOut(50_000, [0x51])], [0x51]) : null;
            using var commitOutputs = (replacement ?? wantsOutputs).CommitOutputs();
            using var inputs = commitOutputs.Save(persister);
            var receiverKey = _receiverInputKey;
            using var inputPair = contributeReceiver
                ? InputPair.NewP2wpkh(new global::Payjoin.TxOut(60_000, receiverKey.PubKey.WitHash.ScriptPubKey.ToBytes()),
                    new global::Payjoin.OutPoint(uint256.One.ToString(), 0))
                : null;
            using var withInput = inputPair is not null ? inputs.ContributeInputs([inputPair]) : null;
            using var commitInputs = (withInput ?? inputs).CommitInputs();
            using var feeRange = commitInputs.Save(persister);
            using var fees = feeRange.ApplyFeeRange(null, null);
            using var provisional = fees.Save(persister);
            using var finalization = provisional.FinalizeProposal(contributeReceiver ? new ReceiverInputSigner(receiverKey) : Signer);
            return finalization.Save(persister);
        }

        public void RestoreState(string stateName)
        {
            var events = Events();
            var count = Enumerable.Range(1, events.Length).First(length =>
            {
                using var replay = PayjoinMethods.ReplayReceiverEventLog(new PrefixPersister(events[..length]));
                using var state = replay.State();
                return state.GetType().Name == stateName;
            });
            using var db = Factory.CreateContext();
            db.ReceiverSessionEvents.RemoveRange(db.ReceiverSessionEvents.Where(e => e.InvoiceId == "invoice" && e.Sequence > count));
            db.SaveChanges();
            using var restored = Replay();
            using var restoredState = restored.State();
            Assert.Equal(stateName, restoredState.GetType().Name);
        }

        private async Task<byte[]> SendNativeRequestAsync(Request request)
        {
            using var content = new ByteArrayContent(request.Body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(request.ContentType);
            using var response = await _http.PostAsync(new Uri(request.Url), content).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }

        public void Dispose()
        {
            Receiver.Dispose();
            _http.Dispose();
            Factory.Dispose();
            Services.Dispose();
            _key.Dispose();
            _receiverInputKey.Dispose();
        }
    }

    private sealed class ReplayingGuard(PayjoinReceiverSessionStore store, byte[] script) : IPayjoinReceiverSessionGuard
    {
        public Task<PayjoinReceiverSessionGuardResult?> TryPrepareAsync(PayjoinReceiverSessionState session, CancellationToken cancellationToken)
        {
            var persister = store.CreatePersister(session);
            var replay = PayjoinMethods.ReplayReceiverEventLog(persister);
            var state = replay.State();
            store.CompleteOwnershipLookupIfFinished(session, state);
            var result = new PayjoinReceiverSessionGuardResult(session, persister, script, replay, state, _ => false);
            using var history = replay.SessionHistory();
            if (history.FallbackTx() is { Length: > 0 } bytes)
                result.StateContext.OriginalTransaction = PayjoinReceiverStateProcessor.ExtractTransactionFacts(bytes);
            return Task.FromResult<PayjoinReceiverSessionGuardResult?>(result);
        }
    }

    private sealed class FaultingWallet : IPayjoinWalletOwnershipService
    {
        public bool FailInputLookup { get; set; }
        public PayjoinInputOwnership InputOwnership { get; set; } = PayjoinInputOwnership.Foreign;
        public Func<NBitcoin.OutPoint, PayjoinInputOwnership>? InputOwnershipLookup { get; set; }
        public Func<Task>? BeforeInputLookup { get; set; }
        public int InputLookupCalls { get; private set; }
        public async Task<PayjoinOwnershipLookupResult> CreateResolverAsync(PayjoinReceiverSessionState session,
            PayjoinOriginalTransactionFacts originalTransaction, CancellationToken cancellationToken)
        {
            InputLookupCalls++;
            if (BeforeInputLookup is not null)
            {
                await BeforeInputLookup().ConfigureAwait(false);
            }
            if (FailInputLookup) throw new HttpRequestException("injected wallet lookup failure");
            return PayjoinOwnershipLookupResult.Ready(new PayjoinInputOwnershipResolver(
                originalTransaction.InputOutpoints.Distinct().ToDictionary(input => input, input => InputOwnershipLookup?.Invoke(input) ?? InputOwnership)));
        }
    }

    private sealed class ExplorerWallet(PayjoinWalletOwnershipServiceTests.OwnershipFixture explorer) : IPayjoinWalletOwnershipService
    {
        public async Task<PayjoinOwnershipLookupResult> CreateResolverAsync(PayjoinReceiverSessionState session,
            PayjoinOriginalTransactionFacts originalTransaction, CancellationToken cancellationToken) =>
            PayjoinOwnershipLookupResult.Ready(await explorer.ResolveAsync(originalTransaction.InputOutpoints, cancellationToken).ConfigureAwait(false));

    }

    private sealed class RejectBroadcast : CanBroadcast
    {
        public bool Callback(byte[] tx) => false;
    }
    private sealed class ForeignInput : IsInputOwned
    {
        public bool Callback(global::Payjoin.OutPoint outpoint) => false;
    }
    private sealed class ReceiverOutput(byte[] receiverScript) : IsScriptOwned
    {
        public bool Callback(byte[] script) => script.AsSpan().SequenceEqual(receiverScript);
    }
    private sealed class RecordingProposalSigner(ProcessPsbt signer) : IPayjoinReceiverProposalSigner
    {
        public int Calls { get; private set; }
        public ProcessPsbt Signer { get; set; } = signer;

        public Task<ProcessPsbt> CreateContributedInputSignerAsync(string storeId, ReceivedCoin[] receiverCoins, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Signer);
        }
    }
    private sealed class PassthroughSigner : ProcessPsbt
    {
        public string Callback(string psbt) => psbt;
    }
    private sealed class ReceiverInputSigner(Key key) : ProcessPsbt
    {
        public string Callback(string psbt)
        {
            var parsed = PSBT.Parse(psbt, Network.RegTest);
            parsed.SignWithKeys(key);
            var input = parsed.Inputs.Single(i => i.PrevOut == new OutPoint(uint256.One, 0));
            Assert.True(input.TryFinalizeInput(out _));
            return parsed.ToBase64();
        }
    }
    private sealed class SenderPersister : JsonSenderSessionPersister
    {
        private readonly List<string> _events = [];
        public void Save(string @event) => _events.Add(@event);
        public string[] Load() => _events.ToArray();
        public void Close() { }
    }

    private sealed class LocalMailroomHandler : DelegatingHandler
    {
        public Uri HealthyRelay { get; }
        public byte[]? ResponseBody { get; set; }
        public List<(HttpStatusCode Status, int Size)> Responses { get; } = [];

        public LocalMailroomHandler(TestServices services)
        {
            var cert = services.Cert();
            InnerHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (request, certificate, _, _) =>
                    request.RequestUri!.Host == "localhost" && certificate is not null && certificate.RawData.AsSpan().SequenceEqual(cert)
            };
            HealthyRelay = new UriBuilder(services.OhttpRelayUrl()) { Scheme = "https" }.Uri;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (ResponseBody is not null)
            {
                Responses.Add((HttpStatusCode.OK, ResponseBody.Length));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ResponseBody) };
            }

            if (request.RequestUri!.Host == "localhost" && request.RequestUri.Port == HealthyRelay.Port)
            {
                request.RequestUri = new UriBuilder(request.RequestUri) { Scheme = "http" }.Uri;
            }

            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            Responses.Add((response.StatusCode, body.Length));
            return response;
        }
    }
}
