using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NSubstitute;
using IPayjoinProposal = Payjoin.IPayjoinProposal;
using NBXplorer;
using BTCPayServer.Logging;
using OutPoint = NBitcoin.OutPoint;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class PayjoinReceiverSessionStoreRelationalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProtocolOwnerCannotAdoptCompetingProgressAfterAWriteConflict(bool reserve)
    {
        using var fixture = new RelationalPluginTestContext();
        var store = fixture.CreateStore();
        var session = CreateSession(store, "owner-conflict");
        var owner = store.CreatePersister(session);
        fixture.BeforeNextSaveChanges = () => store.Owner(session.InvoiceId).Save("competing event");
        Assert.Throws<PayjoinSessionStaleException>(() =>
        {
            if (reserve) store.TryReserveContributedInput(owner, session.StoreId, new OutPoint(uint256.One, 0), DateTimeOffset.UtcNow.AddMinutes(1));
            else store.AppendEvents(owner, ["losing event"]);
        });
        Assert.Equal(session.AttemptVersion, owner.AttemptVersion);
        Assert.Throws<PayjoinSessionStaleException>(() => owner.Load());
        using var db = fixture.CreateDbContext();
        Assert.Empty(db.ReceiverInputReservations);
        Assert.Equal(new[] { "bootstrap-event", "competing event" }, db.ReceiverSessionEvents.OrderBy(e => e.Sequence).Select(e => e.Event).ToArray());
    }

    private const int ConcurrencyRetryEventId = 1;
    private const int ConcurrencyExhaustedEventId = 2;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeldFailuresAndExpiredArmedSessionsAreExcludedBeforeEventQueries(bool expired)
    {
        using var factory = new SqliteTestPayjoinPluginDbContextFactory();
        var detector = new SqliteUniqueConstraintViolationDetector();
        var store = new PayjoinReceiverSessionStore(factory, detector);
        var session = CreateSession(store, "invoice-held");
        store.StorePayjoinUri(session.InvoiceId, session.ReceiverAddress, "bitcoin:receiver?amount=0.1&pj=https://example.com/session");
        var bridges = new PayjoinAccountingBridgeService(factory, new PayjoinSessionBuildLock());
        Assert.True(store.TryReserveContributedInput(store.Owner(session.InvoiceId), session.StoreId, new OutPoint(uint256.One, 0), DateTimeOffset.UtcNow.AddMinutes(1)));
        await factory.SeedBridgeAsync(new CreatePayjoinAccountingBridgeRequest(session.InvoiceId, session.StoreId, "BTC", "BTC-CHAIN",
            DateTimeOffset.UtcNow.AddHours(-7), ExpectedFinalTransactionId: expired ? uint256.One.ToString() : null), CancellationToken.None);
        if (expired) await bridges.ExpirePendingAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        else await bridges.MarkFailedAsync(session.InvoiceId, "unrelated opaque reason", CancellationToken.None);
        var queries = new List<string>();
        factory.OnReaderExecuting = queries.Add;
        Assert.Empty(store.GetSessions());
        Assert.DoesNotContain(queries, sql => sql.Contains("ReceiverSessionEvents", StringComparison.Ordinal));
        Assert.False(store.RemoveSession(session.InvoiceId));
        Assert.False(store.TryRemoveSessionUnlessNegotiating(session.InvoiceId));
        Assert.Null(store.GetServablePayjoinUri(session.InvoiceId, session.ReceiverAddress));
        Assert.True(store.TryGetSession(session.InvoiceId, out var held));
        Assert.Equal(session.GetEvents(), held!.GetEvents());
        Assert.NotNull(held.PayjoinUri);
        Assert.Equal(held.GetEvents(), store.GetAttempt(session.InvoiceId, session.SessionInstanceId).GetEvents());
        Assert.Equal(1, store.CleanupExpiredInputReservations(DateTimeOffset.UtcNow.AddMinutes(2)));
        Assert.Equal(held.PayjoinUri, store.GetAttempt(session.InvoiceId, session.SessionInstanceId).PayjoinUri);
        Assert.Equal(held.GetEvents(), store.GetAttempt(session.InvoiceId, session.SessionInstanceId).GetEvents());
        Assert.Contains(queries, sql => sql.Contains("ReceiverSessionEvents", StringComparison.Ordinal));
        CreateSession(store, "invoice-pollable");
        var pollable = Assert.Single(store.GetSessions());
        Assert.Equal("invoice-pollable", pollable.InvoiceId);
        var retry = await bridges.TryRetryAsync(session.InvoiceId, session.StoreId, DateTimeOffset.UtcNow, CancellationToken.None);
        if (!expired) { Assert.Null(retry); return; }
        Assert.NotNull(retry);
        Assert.Contains(await bridges.GetPendingReconciliationAsync(DateTimeOffset.UtcNow, CancellationToken.None), bridge => bridge.InvoiceId == session.InvoiceId);
        Assert.Contains(store.GetSessions(), row => row.InvoiceId == session.InvoiceId);
        await bridges.MarkReconciledAsync(session.InvoiceId, null, null, null, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.True(store.RemoveSession(session.InvoiceId, session.SessionInstanceId));
        Assert.False(store.TryGetSession(session.InvoiceId, out _));
    }

    [Theory]
    [InlineData("unarmed")]
    [InlineData("empty-expected")]
    [InlineData("reconciled")]
    public async Task ExpiredDeadlineDoesNotExcludeSessionsNeededForCleanup(string kind)
    {
        using var fixture = new RelationalPluginTestContext();
        var store = fixture.CreateStore();
        var bridge = fixture.CreateBridgeService();
        var session = store.GetOrCreateSession("cleanup", "receiver", "store", DateTimeOffset.UtcNow.AddMinutes(-1), ["retained event"]);
        await fixture.SeedBridgeAsync(new CreatePayjoinAccountingBridgeRequest("cleanup", "store", "BTC", "BTC-CHAIN", DateTimeOffset.UtcNow.AddHours(-7),
            ExpectedFinalTransactionId: kind == "unarmed" ? null : kind == "empty-expected" ? "" : uint256.One.ToString()), CancellationToken.None);
        if (kind == "reconciled") await bridge.MarkReconciledAsync("cleanup", null, null, null, DateTimeOffset.UtcNow, CancellationToken.None);
        else await bridge.ExpirePendingAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(session.GetEvents(), Assert.Single(store.GetSessions()).GetEvents());
        if (kind != "empty-expected") Assert.True(store.RemoveSession("cleanup", session.SessionInstanceId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemoveSessionClearsRelayQuarantineOnlyAfterSuccessfulDeletion(bool failSave)
    {
        using var factory = new SqliteTestPayjoinPluginDbContextFactory();
        var manager = new PayjoinMailroomManager(Microsoft.Extensions.Logging.Abstractions.NullLogger<PayjoinMailroomManager>.Instance,
            TimeSpan.FromMinutes(10), (_, _, _, _) => throw new NotSupportedException());
        var store = new PayjoinReceiverSessionStore(factory, new SqliteUniqueConstraintViolationDetector(), manager);
        var session = CreateSession(store, "invoice-quarantine");
        var relay = new Uri("https://relay.example/");
        manager.MarkRelayFailedForSession(session.InvoiceId, relay);
        manager.MarkRelayFailedForSession("another-invoice", relay);
        factory.FailSaveChanges = failSave;

        if (failSave)
        {
            Assert.Throws<Microsoft.EntityFrameworkCore.DbUpdateException>(() => store.RemoveSession(session.InvoiceId));
            factory.FailSaveChanges = false;
            Assert.True(store.TryGetSession(session.InvoiceId, out _));
            Assert.Null(manager.ChooseRelayForRequest([relay], session.InvoiceId));
        }
        else
        {
            Assert.True(store.RemoveSession(session.InvoiceId));
            Assert.False(store.TryGetSession(session.InvoiceId, out _));
            Assert.Equal(relay, manager.ChooseRelayForRequest([relay], session.InvoiceId));
        }

        Assert.Null(manager.ChooseRelayForRequest([relay], "another-invoice"));
    }

    [Fact]
    public void RemoveMissingSessionClearsItsRelayQuarantine()
    {
        using var factory = new SqliteTestPayjoinPluginDbContextFactory();
        var manager = new PayjoinMailroomManager(Microsoft.Extensions.Logging.Abstractions.NullLogger<PayjoinMailroomManager>.Instance,
            TimeSpan.FromMinutes(10), (_, _, _, _) => throw new NotSupportedException());
        var store = new PayjoinReceiverSessionStore(factory, new SqliteUniqueConstraintViolationDetector(), manager);
        var relay = new Uri("https://relay.example/");
        manager.MarkRelayFailedForSession("missing-invoice", relay);

        Assert.False(store.RemoveSession("missing-invoice"));
        Assert.Equal(relay, manager.ChooseRelayForRequest([relay], "missing-invoice"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void GuardedSessionRemovalClearsRelayQuarantineOnlyAfterSuccessfulDeletion(bool failSave, bool contributedInput)
    {
        using var factory = new SqliteTestPayjoinPluginDbContextFactory();
        var manager = new PayjoinMailroomManager(Microsoft.Extensions.Logging.Abstractions.NullLogger<PayjoinMailroomManager>.Instance,
            TimeSpan.FromMinutes(10), (_, _, _, _) => throw new NotSupportedException());
        var store = new PayjoinReceiverSessionStore(factory, new SqliteUniqueConstraintViolationDetector(), manager);
        var session = CreateSession(store, "invoice-guarded-quarantine");
        var relay = new Uri("https://relay.example/");
        manager.MarkRelayFailedForSession(session.InvoiceId, relay);
        manager.MarkRelayFailedForSession("another-invoice", relay);

        if (contributedInput)
        {
            var outPoint = new OutPoint(uint256.Parse("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), 0);
            Assert.True(store.TryReserveContributedInput(store.Owner(session.InvoiceId), session.StoreId, outPoint, DateTimeOffset.UtcNow.AddMinutes(10)));
            Assert.False(store.TryRemoveSessionUnlessNegotiating(session.InvoiceId));
            Assert.True(store.TryGetSession(session.InvoiceId, out _));
            Assert.Null(manager.ChooseRelayForRequest([relay], session.InvoiceId));
        }
        else if (failSave)
        {
            factory.FailSaveChanges = true;
            Assert.Throws<DbUpdateException>(() => store.TryRemoveSessionUnlessNegotiating(session.InvoiceId));
            factory.FailSaveChanges = false;
            Assert.True(store.TryGetSession(session.InvoiceId, out _));
            Assert.Null(manager.ChooseRelayForRequest([relay], session.InvoiceId));
        }
        else
        {
            Assert.True(store.TryRemoveSessionUnlessNegotiating(session.InvoiceId));
            Assert.False(store.TryGetSession(session.InvoiceId, out _));
            Assert.Equal(relay, manager.ChooseRelayForRequest([relay], session.InvoiceId));
        }

        Assert.Null(manager.ChooseRelayForRequest([relay], "another-invoice"));
    }

    [Fact]
    public void GuardedRemovalOfMissingSessionClearsItsRelayQuarantine()
    {
        using var factory = new SqliteTestPayjoinPluginDbContextFactory();
        var manager = new PayjoinMailroomManager(Microsoft.Extensions.Logging.Abstractions.NullLogger<PayjoinMailroomManager>.Instance,
            TimeSpan.FromMinutes(10), (_, _, _, _) => throw new NotSupportedException());
        var store = new PayjoinReceiverSessionStore(factory, new SqliteUniqueConstraintViolationDetector(), manager);
        var relay = new Uri("https://relay.example/");
        manager.MarkRelayFailedForSession("missing-invoice", relay);

        Assert.True(store.TryRemoveSessionUnlessNegotiating("missing-invoice"));
        Assert.Equal(relay, manager.ChooseRelayForRequest([relay], "missing-invoice"));
    }

    [Fact]
    public void WriteWithConcurrencyRetryRetriesOnceThenSucceedsAndLogsTheRetry()
    {
        using var testContext = new RelationalPluginTestContext();
        var logger = new CapturingLogger<PayjoinReceiverSessionStore>();
        var store = testContext.CreateStore(logger);
        var calls = 0;

        var result = store.WriteWithConcurrencyRetry(_ =>
        {
            calls++;
            if (calls == 1)
            {
                throw new DbUpdateConcurrencyException("simulated concurrent write");
            }

            return "written";
        });

        Assert.Equal("written", result);
        Assert.Equal(2, calls);
        Assert.Equal(1, logger.Entries.Count(id => id == ConcurrencyRetryEventId));
        Assert.DoesNotContain(ConcurrencyExhaustedEventId, logger.Entries);
    }

    [Fact]
    public void WriteWithConcurrencyRetryRethrowsAndWarnsWhenRetriesAreExhausted()
    {
        using var testContext = new RelationalPluginTestContext();
        var logger = new CapturingLogger<PayjoinReceiverSessionStore>();
        var store = testContext.CreateStore(logger);
        var calls = 0;

        Assert.Throws<DbUpdateConcurrencyException>(() =>
            store.WriteWithConcurrencyRetry<string>(_ =>
            {
                calls++;
                throw new DbUpdateConcurrencyException("always conflicts");
            }));

        Assert.Equal(3, calls);
        Assert.Equal(2, logger.Entries.Count(id => id == ConcurrencyRetryEventId));
        Assert.Equal(1, logger.Entries.Count(id => id == ConcurrencyExhaustedEventId));
    }

    [Fact]
    public void TransientFailureDuringSeenInputCheckDoesNotPoisonRetryOfTheSameAttempt()
    {
        using var testContext = new RelationalPluginTestContext();
        var session = CreateSession(testContext.CreateStore(), "invoice-seen-input-transient");
        var store = testContext.CreateSeenInputStore();
        var firstInput = new global::Payjoin.OutPoint(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            0);
        var secondInput = new global::Payjoin.OutPoint(
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            1);

        Assert.Throws<global::Payjoin.ReceiverPersistedException.Transient>(() =>
            store.ExecuteSeenInputTransition<TestTransitionState>(testContext.CreateStore().Owner(session.InvoiceId), (callback, _) =>
                {
                    Assert.False(callback.Callback(firstInput));
                    throw new global::Payjoin.ReceiverPersistedException.Transient(
                        new global::Payjoin.ReceiverException.Unexpected());
                }));

        using (var failedAttemptContext = testContext.CreateDbContext())
        {
            Assert.Empty(failedAttemptContext.ReceiverSeenInputs);
            Assert.Equal(
                new[] { "bootstrap-event" },
                failedAttemptContext.ReceiverSessionEvents
                    .Where(x => x.InvoiceId == session.InvoiceId)
                    .OrderBy(x => x.Sequence)
                    .Select(x => x.Event)
                    .ToArray());
        }

        var wasSeenBeforeRetry = true;
        using var retryState = store.ExecuteSeenInputTransition<TestTransitionState>(testContext.CreateStore().Owner(session.InvoiceId), (callback, persister) =>
            {
                wasSeenBeforeRetry = callback.Callback(firstInput);
                Assert.False(callback.Callback(secondInput));
                persister.Save("checked-no-inputs-seen-before");
                return new TestTransitionState();
            });

        Assert.False(wasSeenBeforeRetry);
        using var committedContext = testContext.CreateDbContext();
        Assert.Equal(2, committedContext.ReceiverSeenInputs.Count());
        Assert.Equal(
            new[] { "bootstrap-event", "checked-no-inputs-seen-before" },
            committedContext.ReceiverSessionEvents
                .Where(x => x.InvoiceId == session.InvoiceId)
                .OrderBy(x => x.Sequence)
                .Select(x => x.Event)
                .ToArray());
    }

    [Fact]
    public void SeenInputCommittedByAnotherSessionIsStillReportedAsSeen()
    {
        using var testContext = new RelationalPluginTestContext();
        var sessionStore = testContext.CreateStore();
        var firstSession = CreateSession(sessionStore, "invoice-seen-input-first");
        var secondSession = CreateSession(sessionStore, "invoice-seen-input-second");
        var store = testContext.CreateSeenInputStore();
        var input = new global::Payjoin.OutPoint(
            "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            2);

        using var firstState = store.ExecuteSeenInputTransition<TestTransitionState>(testContext.CreateStore().Owner(firstSession.InvoiceId), (callback, persister) =>
            {
                Assert.False(callback.Callback(input));
                persister.Save("first-session-checked-inputs");
                return new TestTransitionState();
            });

        var wasSeenBySecondSession = false;
        using var secondState = store.ExecuteSeenInputTransition<TestTransitionState>(testContext.CreateStore().Owner(secondSession.InvoiceId), (callback, persister) =>
            {
                wasSeenBySecondSession = callback.Callback(input);
                persister.Save("second-session-rejected-input");
                return new TestTransitionState();
            });

        Assert.True(wasSeenBySecondSession);
        using var context = testContext.CreateDbContext();
        Assert.Single(context.ReceiverSeenInputs);
    }

    [Fact]
    public void SeenInputsDoNotSurviveAFailedSessionEventWrite()
    {
        using var testContext = new RelationalPluginTestContext();
        var session = CreateSession(testContext.CreateStore(), "invoice-seen-input-rollback");
        var store = testContext.CreateSeenInputStore();
        var input = new global::Payjoin.OutPoint(
            "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
            3);

        testContext.BeforeNextSaveChanges = () =>
        {
            using var competingInstance = testContext.CreateDbContext();
            competingInstance.ReceiverSessionEvents.Add(new Data.PayjoinReceiverSessionEventData
            {
                InvoiceId = session.InvoiceId,
                Sequence = 2,
                Event = "event-from-a-concurrent-instance",
                CreatedAt = DateTimeOffset.UtcNow
            });
            competingInstance.SaveChanges();
        };

        Assert.Throws<Microsoft.EntityFrameworkCore.DbUpdateException>(() =>
            store.ExecuteSeenInputTransition<TestTransitionState>(testContext.CreateStore().Owner(session.InvoiceId), (callback, persister) =>
                {
                    Assert.False(callback.Callback(input));
                    persister.Save("event-that-must-roll-back");
                    return new TestTransitionState();
                }));

        using var context = testContext.CreateDbContext();
        Assert.Empty(context.ReceiverSeenInputs);
        Assert.Equal(
            new[] { "bootstrap-event", "event-from-a-concurrent-instance" },
            context.ReceiverSessionEvents
                .Where(x => x.InvoiceId == session.InvoiceId)
                .OrderBy(x => x.Sequence)
                .Select(x => x.Event)
                .ToArray());
    }

    [Fact]
    public void SeenInputsSurviveTheReplyableErrorThatRejectsTheProposal()
    {
        // Arrange
        using var testContext = new RelationalPluginTestContext();
        var session = CreateSession(testContext.CreateStore(), "invoice-seen-input-replyable-error");
        var store = testContext.CreateSeenInputStore();
        var freshInput = new global::Payjoin.OutPoint(
            "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
            4);
        var inputSeenByAnEarlierSession = new global::Payjoin.OutPoint(
            "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
            5);

        // Act
        using (var seed = testContext.CreateDbContext())
        {
            seed.ReceiverSeenInputs.Add(new Data.PayjoinReceiverSeenInputData
            {
                TransactionId = inputSeenByAnEarlierSession.Txid,
                OutputIndex = checked((long)inputSeenByAnEarlierSession.Vout),
                SeenAt = DateTimeOffset.UtcNow
            });
            seed.SaveChanges();
        }

        // Assert
        Assert.Throws<global::Payjoin.ReceiverPersistedException.Fatal>(() =>
            store.ExecuteSeenInputTransition<TestTransitionState>(testContext.CreateStore().Owner(session.InvoiceId), (callback, persister) =>
                {
                    Assert.False(callback.Callback(freshInput));
                    Assert.True(callback.Callback(inputSeenByAnEarlierSession));
                    persister.Save("got-replyable-error");
                    throw new global::Payjoin.ReceiverPersistedException.Fatal(
                        new global::Payjoin.ReceiverException.Unexpected());
                }));

        using var context = testContext.CreateDbContext();
        Assert.Equal(
            new[] { inputSeenByAnEarlierSession.Txid, freshInput.Txid },
            context.ReceiverSeenInputs.OrderBy(x => x.Id).Select(x => x.TransactionId).ToArray());
        Assert.Equal(
            new[] { "bootstrap-event", "got-replyable-error" },
            context.ReceiverSessionEvents
                .Where(x => x.InvoiceId == session.InvoiceId)
                .OrderBy(x => x.Sequence)
                .Select(x => x.Event)
                .ToArray());
    }

    [Fact]
    public void TryReserveContributedInputAllowsOnlyOneReservationPerOutPointOnRelationalProvider()
    {
        // Arrange
        using var testContext = new RelationalPluginTestContext();
        var firstStore = testContext.CreateStore();
        var secondStore = testContext.CreateStore();
        var firstSession = CreateSession(firstStore, "invoice-relational-first");
        var secondSession = CreateSession(secondStore, "invoice-relational-second");
        var outPoint = new OutPoint(uint256.Parse("dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"), 1);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);

        // Act
        Assert.True(firstStore.TryReserveContributedInput(firstStore.Owner(firstSession.InvoiceId), firstSession.StoreId, outPoint, expiresAt));
        Assert.False(secondStore.TryReserveContributedInput(secondStore.Owner(secondSession.InvoiceId), secondSession.StoreId, outPoint, expiresAt));

        // Assert
        using var context = testContext.CreateDbContext();
        var reservation = Assert.Single(context.ReceiverInputReservations);
        Assert.Equal(firstSession.InvoiceId, reservation.InvoiceId);
        Assert.Equal(outPoint.Hash.ToString(), reservation.TransactionId);
        Assert.Equal((long)outPoint.N, reservation.OutputIndex);
    }

    [Fact]
    public async Task TryReserveContributedInputAllowsOnlyOneWinnerUnderConcurrentRequestsOnRelationalProvider()
    {
        // Arrange
        using var testContext = new RelationalPluginTestContext();
        var firstStore = testContext.CreateStore();
        var secondStore = testContext.CreateStore();
        var firstSession = CreateSession(firstStore, "invoice-relational-concurrent-first");
        var secondSession = CreateSession(secondStore, "invoice-relational-concurrent-second");
        var outPoint = new OutPoint(uint256.Parse("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), 2);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);

        using var start = new ManualResetEventSlim(false);
        var firstAttempt = Task.Run(() =>
        {
            start.Wait();
            return firstStore.TryReserveContributedInput(firstStore.Owner(firstSession.InvoiceId), firstSession.StoreId, outPoint, expiresAt);
        });
        var secondAttempt = Task.Run(() =>
        {
            start.Wait();
            return secondStore.TryReserveContributedInput(secondStore.Owner(secondSession.InvoiceId), secondSession.StoreId, outPoint, expiresAt);
        });

        // Act
        start.Set();
        var results = await Task.WhenAll(firstAttempt, secondAttempt).ConfigureAwait(true);

        // Assert
        Assert.Single(results, result => result);
        Assert.Single(results, result => !result);

        using var context = testContext.CreateDbContext();
        var reservation = Assert.Single(context.ReceiverInputReservations);
        Assert.Equal(outPoint.Hash.ToString(), reservation.TransactionId);
        Assert.Equal((long)outPoint.N, reservation.OutputIndex);
        Assert.Contains(reservation.InvoiceId, new[] { firstSession.InvoiceId, secondSession.InvoiceId });
    }

    [Fact]
    public void CommitReceiverOutputsWritesEventsAndBridgeTogether()
    {
        using var testContext = new RelationalPluginTestContext();
        var store = testContext.CreateStore();
        var session = CreateSession(store, "invoice-atomic-append");
        using (var seed = testContext.CreateDbContext())
        {
            seed.AccountingBridges.Add(new Data.PayjoinAccountingBridgeData
            {
                InvoiceId = session.InvoiceId,
                StoreId = session.StoreId,
                CryptoCode = PayjoinConstants.BitcoinCode,
                PaymentMethodId = "BTC-BTC",
                EffectiveInvoiceValueSats = 1234,
                Status = Data.PayjoinAccountingBridgeStatus.PendingFallback,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            seed.SaveChanges();
        }

        store.CommitReceiverOutputs(store.CreatePersister(session), ["event-a", "event-b"],
            new PayjoinReceiverOutputBuilder.OutputReplacement([], [0xaa, 0xbb], 1234, null));

        using var context = testContext.CreateDbContext();
        var events = context.ReceiverSessionEvents
            .Where(x => x.InvoiceId == session.InvoiceId)
            .OrderBy(x => x.Sequence)
            .ToArray();
        Assert.Equal(new[] { "bootstrap-event", "event-a", "event-b" }, events.Select(x => x.Event).ToArray());
        Assert.Equal(new[] { 1, 2, 3 }, events.Select(x => x.Sequence).ToArray());
        var bridge = Assert.Single(context.AccountingBridges.Where(x => x.InvoiceId == session.InvoiceId));
        Assert.Equal("AABB", bridge.SettlementScript);
        Assert.Equal(1234, bridge.EffectiveInvoiceValueSats);
    }

    [Fact]
    public void CommitReceiverOutputsDoesNotAdvanceWhenRequiredBridgeIsMissing()
    {
        using var testContext = new RelationalPluginTestContext();
        var store = testContext.CreateStore();
        var session = CreateSession(store, "invoice-atomic-no-bridge");

        Assert.Throws<PayjoinAccountingReconciliationDataException>(() => store.CommitReceiverOutputs(store.Owner(session.InvoiceId), ["event-a"], new PayjoinReceiverOutputBuilder.OutputReplacement([], Convert.FromHexString("AABB"), 50_000, null)));

        using var context = testContext.CreateDbContext();
        var events = context.ReceiverSessionEvents
            .Where(x => x.InvoiceId == session.InvoiceId)
            .OrderBy(x => x.Sequence)
            .Select(x => x.Event)
            .ToArray();
        Assert.Equal(new[] { "bootstrap-event" }, events);
        Assert.Empty(context.AccountingBridges.Where(x => x.InvoiceId == session.InvoiceId));
    }

    [Fact]
    public void GetServablePayjoinUriReturnsTheCachedUriForALiveSession()
    {
        using var testContext = new RelationalPluginTestContext();
        var store = testContext.CreateStore();
        var session = CreateSession(store, "invoice-servable-uri");
        store.StorePayjoinUri(session.InvoiceId, ReceiverAddress, PayjoinUri);

        var reloadedStore = testContext.CreateStore();

        Assert.Equal(PayjoinUri, reloadedStore.GetServablePayjoinUri(session.InvoiceId, ReceiverAddress));
    }

    [Fact]
    public void GetServablePayjoinUriReturnsNullForAnUnknownInvoice()
    {
        using var testContext = new RelationalPluginTestContext();
        var store = testContext.CreateStore();

        Assert.Null(store.GetServablePayjoinUri("invoice-never-created", ReceiverAddress));
    }

    [Fact]
    public void GetServablePayjoinUriReturnsNullWhenNoUriHasBeenCached()
    {
        using var testContext = new RelationalPluginTestContext();
        var store = testContext.CreateStore();
        var session = CreateSession(store, "invoice-uncached-uri");

        Assert.Null(store.GetServablePayjoinUri(session.InvoiceId, ReceiverAddress));
    }

    [Fact]
    public void RemovingAllSessionEventsInvalidatesAStaleTrackedUriCacheWrite()
    {
        using var testContext = new RelationalPluginTestContext();
        var store = testContext.CreateStore();
        var session = CreateSession(store, "invoice-stale-uri-cache");

        using var staleWriter = testContext.CreateDbContext();
        var staleRow = staleWriter.ReceiverSessions.Single(x => x.InvoiceId == session.InvoiceId);
        Assert.True(staleWriter.ReceiverSessionEvents.Any(x => x.InvoiceId == session.InvoiceId));

        using (var removalContext = testContext.CreateDbContext())
        {
            var removalRow = removalContext.ReceiverSessions.Single(x => x.InvoiceId == session.InvoiceId);
            PayjoinReceiverSessionStore.RemoveAllSessionEvents(removalContext, removalRow);
            removalContext.SaveChanges();
        }

        staleRow.PayjoinUri = PayjoinUri;
        staleRow.UpdatedAt = DateTimeOffset.UtcNow;

        Assert.Throws<DbUpdateConcurrencyException>(() => staleWriter.SaveChanges());
        Assert.Null(testContext.CreateStore().GetServablePayjoinUri(session.InvoiceId, ReceiverAddress));
    }

    [Fact]
    public void GetServablePayjoinUriReturnsNullForADifferentAddress()
    {
        using var testContext = new RelationalPluginTestContext();
        var store = testContext.CreateStore();
        var session = CreateSession(store, "invoice-other-address");
        store.StorePayjoinUri(session.InvoiceId, ReceiverAddress, PayjoinUri);

        Assert.Null(store.GetServablePayjoinUri(session.InvoiceId, "bcrt1qsomewhereelse"));
    }

    [Fact]
    public void GetServablePayjoinUriReturnsNullForAnExpiredSession()
    {
        using var testContext = new RelationalPluginTestContext();
        var store = testContext.CreateStore();
        store.GetOrCreateSession(
            "invoice-expired-uri",
            ReceiverAddress,
            "store-1",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            ["bootstrap-event"]);
        store.StorePayjoinUri("invoice-expired-uri", ReceiverAddress, PayjoinUri);

        Assert.Null(store.GetServablePayjoinUri("invoice-expired-uri", ReceiverAddress));
    }

    [Fact]
    public void GetServablePayjoinUriReturnsNullForACloseRequestedSession()
    {
        using var testContext = new RelationalPluginTestContext();
        var store = testContext.CreateStore();
        var session = CreateSession(store, "invoice-close-requested-uri");
        store.StorePayjoinUri(session.InvoiceId, ReceiverAddress, PayjoinUri);
        Assert.True(store.RequestClose(session.InvoiceId, InvoiceStatus.Expired));

        Assert.Null(store.GetServablePayjoinUri(session.InvoiceId, ReceiverAddress));
    }

    [Fact]
    public void PayjoinUriIsNotCachedAgainstADifferentAddressThanItWasBuiltFor()
    {
        using var testContext = new RelationalPluginTestContext();
        var store = testContext.CreateStore();
        const string invoiceId = "invoice-address-changed";
        const string otherAddress = "bcrt1qsomeotheraddress00000000000000000000000";

        CreateSession(store, invoiceId);
        Assert.True(store.RemoveSession(invoiceId));
        store.GetOrCreateSession(invoiceId, otherAddress, "store-1", DateTimeOffset.UtcNow.AddMinutes(15), ["bootstrap-event"]);

        store.StorePayjoinUri(invoiceId, ReceiverAddress, PayjoinUri);

        Assert.Null(store.GetServablePayjoinUri(invoiceId, otherAddress));
        using var context = testContext.CreateDbContext();
        Assert.Null(context.ReceiverSessions.Single(x => x.InvoiceId == invoiceId).PayjoinUri);
    }

    [Fact]
    public void SessionWithoutAContributedInputIsRemoved()
    {
        using var testContext = new RelationalPluginTestContext();
        var store = testContext.CreateStore();
        var session = CreateSession(store, "invoice-no-contribution");

        Assert.True(store.TryRemoveSessionUnlessNegotiating(session.InvoiceId));
        Assert.False(store.TryGetSession(session.InvoiceId, out _));
    }

    [Fact]
    public void UnknownInvoiceCountsAsRemoved()
    {
        using var testContext = new RelationalPluginTestContext();

        Assert.True(testContext.CreateStore().TryRemoveSessionUnlessNegotiating("invoice-never-created"));
    }

    [Fact]
    public void SessionWithAContributedInputSurvivesTheDiscard()
    {
        using var testContext = new RelationalPluginTestContext();
        var store = testContext.CreateStore();
        var session = CreateSession(store, "invoice-contributed");
        var outPoint = new OutPoint(uint256.Parse("cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"), 2);
        Assert.True(store.TryReserveContributedInput(store.Owner(session.InvoiceId), session.StoreId, outPoint, DateTimeOffset.UtcNow.AddMinutes(10)));

        Assert.False(store.TryRemoveSessionUnlessNegotiating(session.InvoiceId));
        Assert.True(store.TryGetSession(session.InvoiceId, out _));
        using var context = testContext.CreateDbContext();
        Assert.True(context.ReceiverInputReservations.Any(x => x.InvoiceId == session.InvoiceId));
    }

    [Fact]
    public void InputReservedWhileDiscardIsSavingStillStopsTheDiscard()
    {
        using var testContext = new RelationalPluginTestContext();
        var logger = new CapturingLogger<PayjoinReceiverSessionStore>();
        var store = testContext.CreateStore(logger);
        var session = CreateSession(store, "invoice-contributed-during-discard");
        var outPoint = new OutPoint(uint256.Parse("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), 3);

        testContext.BeforeNextSaveChanges = () =>
            Assert.True(testContext.CreateStore().TryReserveContributedInput(testContext.CreateStore().Owner(session.InvoiceId), session.StoreId, outPoint, DateTimeOffset.UtcNow.AddMinutes(10)));

        Assert.False(store.TryRemoveSessionUnlessNegotiating(session.InvoiceId));
        Assert.True(store.TryGetSession(session.InvoiceId, out var reloaded));
        Assert.True(reloaded!.TryGetContributedInput(out var contributedInput));
        Assert.Equal(outPoint, contributedInput);
        Assert.Contains(ConcurrencyRetryEventId, logger.Entries);
    }

    [Fact]
    public void UnrelatedConcurrentRevisionChangeIsRetriedAndRemoved()
    {
        using var testContext = new RelationalPluginTestContext();
        var logger = new CapturingLogger<PayjoinReceiverSessionStore>();
        var store = testContext.CreateStore(logger);
        var session = CreateSession(store, "invoice-revision-changed-during-discard");

        testContext.BeforeNextSaveChanges = () =>
        {
            using var concurrentContext = testContext.CreateDbContext();
            var concurrentRow = concurrentContext.ReceiverSessions.Single(x => x.InvoiceId == session.InvoiceId);
            concurrentRow.AttemptVersion = checked(concurrentRow.AttemptVersion + 1);
            concurrentContext.SaveChanges();
        };

        Assert.True(store.TryRemoveSessionUnlessNegotiating(session.InvoiceId));
        Assert.False(store.TryGetSession(session.InvoiceId, out _));
        Assert.Contains(ConcurrencyRetryEventId, logger.Entries);
    }

    [Fact]
    public void DiscardThrowsRatherThanGuessingWhenConcurrencyRetriesAreExhausted()
    {
        using var testContext = new RelationalPluginTestContext();
        var logger = new CapturingLogger<PayjoinReceiverSessionStore>();
        var store = testContext.CreateStore(logger);
        var session = CreateSession(store, "invoice-revision-always-changing");

        void BumpRevisionConcurrently()
        {
            using (var concurrentContext = testContext.CreateDbContext())
            {
                var concurrentRow = concurrentContext.ReceiverSessions.Single(x => x.InvoiceId == session.InvoiceId);
                concurrentRow.AttemptVersion = checked(concurrentRow.AttemptVersion + 1);
                concurrentContext.SaveChanges();
            }

            testContext.BeforeNextSaveChanges = BumpRevisionConcurrently;
        }

        testContext.BeforeNextSaveChanges = BumpRevisionConcurrently;

        Assert.Throws<DbUpdateConcurrencyException>(() => store.TryRemoveSessionUnlessNegotiating(session.InvoiceId));
        Assert.Contains(ConcurrencyExhaustedEventId, logger.Entries);
        Assert.True(testContext.CreateStore().TryGetSession(session.InvoiceId, out _));
    }

    [Fact]
    public void SessionWithAnUnparseableRecordedInputSurvivesTheDiscard()
    {
        using var testContext = new RelationalPluginTestContext();
        var store = testContext.CreateStore();
        var session = CreateSession(store, "invoice-malformed-contribution");

        using (var context = testContext.CreateDbContext())
        {
            var row = context.ReceiverSessions.Single(x => x.InvoiceId == session.InvoiceId);
            row.ContributedInputTransactionId = "not-a-txid";
            row.ContributedInputOutputIndex = 0;
            context.SaveChanges();
        }

        Assert.True(testContext.CreateStore().TryGetSession(session.InvoiceId, out var reloaded));
        Assert.False(reloaded!.TryGetContributedInput(out _));
        Assert.False(store.TryRemoveSessionUnlessNegotiating(session.InvoiceId));
    }

    [Fact]
    public void SessionWritesStillSucceedAfterAReservationAdvancedTheConcurrencyToken()
    {
        using var testContext = new RelationalPluginTestContext();
        var store = testContext.CreateStore();
        var session = CreateSession(store, "invoice-token-advanced");
        var outPoint = new OutPoint(uint256.Parse("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), 7);

        Assert.True(store.TryReserveContributedInput(store.Owner(session.InvoiceId), session.StoreId, outPoint, DateTimeOffset.UtcNow.AddMinutes(10)));

        Assert.True(store.RequestClose(session.InvoiceId, InvoiceStatus.Expired));
        Assert.True(store.TryConsumeInitializedPollAfterCloseRequest(session.InvoiceId));
        store.AppendEvents(store.Owner(session.InvoiceId), ["event-after-reservation"]);
        Assert.Equal(1, store.CleanupExpiredInputReservations(DateTimeOffset.UtcNow.AddMinutes(20)));
        Assert.True(store.RemoveSession(session.InvoiceId));
        Assert.False(store.TryGetSession(session.InvoiceId, out _));
    }

    [Fact]
    public void InputReservedAfterTheRowWasReadStillStopsTheDiscard()
    {
        using var testContext = new RelationalPluginTestContext();
        var store = testContext.CreateStore();
        var session = CreateSession(store, "invoice-contributed-late");

        using var readContext = testContext.CreateDbContext();
        var staleRow = readContext.ReceiverSessions.Single(x => x.InvoiceId == session.InvoiceId);

        var outPoint = new OutPoint(uint256.Parse("dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"), 1);
        Assert.True(store.TryReserveContributedInput(store.Owner(session.InvoiceId), session.StoreId, outPoint, DateTimeOffset.UtcNow.AddMinutes(10)));

        Assert.Null(staleRow.ContributedInputTransactionId);
        PayjoinReceiverSessionStore.RemoveAllSessionEvents(readContext, staleRow);
        readContext.ReceiverSessions.Remove(staleRow);
        Assert.Throws<DbUpdateConcurrencyException>(() => readContext.SaveChanges());

        Assert.True(store.TryGetSession(session.InvoiceId, out _));
    }

    [Fact]
    public async Task DestructiveAndProtectingWritesAdvanceTheAttemptVersion()
    {
        using var testContext = new RelationalPluginTestContext();
        var store = testContext.CreateStore();
        var session = CreateSession(store, "invoice-version-contract");

        int ReadVersion()
        {
            using var context = testContext.CreateDbContext();
            return context.ReceiverSessions.Single(x => x.InvoiceId == session.InvoiceId).AttemptVersion;
        }

        var initialVersion = ReadVersion();

        store.AppendEvents(store.Owner(session.InvoiceId), ["version-contract-event"]);
        store.StorePayjoinUri(session.InvoiceId, ReceiverAddress, PayjoinUri);
        Assert.True(store.RequestClose(session.InvoiceId, InvoiceStatus.Expired));
        Assert.True(store.TryConsumeInitializedPollAfterCloseRequest(session.InvoiceId));
        Assert.Equal(initialVersion + 1, ReadVersion());

        var outPoint = new OutPoint(uint256.Parse("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"), 5);
        Assert.True(store.TryReserveContributedInput(store.Owner(session.InvoiceId), session.StoreId, outPoint, DateTimeOffset.UtcNow.AddMinutes(10)));
        Assert.Equal(initialVersion + 2, ReadVersion());

        Assert.Equal(1, store.CleanupExpiredInputReservations(DateTimeOffset.UtcNow.AddMinutes(20)));
        Assert.Equal(initialVersion + 3, ReadVersion());

        var bridges = testContext.CreateBridgeService();
        var transaction = Network.RegTest.CreateTransaction();
        transaction.Inputs.Add(new OutPoint(uint256.One, 0));
        transaction.Outputs.Add(Money.Satoshis(123), Script.FromHex("51"));
        await testContext.SeedBridgeAsync(new CreatePayjoinAccountingBridgeRequest(session.InvoiceId, session.StoreId, "BTC", "BTC-CHAIN", session.MonitoringExpiresAt, EffectiveInvoiceValueSats: 123), CancellationToken.None);
        store.CommitReceiverOutputs(store.Owner(session.InvoiceId), ["outputs"], new PayjoinReceiverOutputBuilder.OutputReplacement([], Convert.FromHexString("51"), 123, null));
        Assert.Equal(initialVersion + 4, ReadVersion());
        store.CommitProposal(store.Owner(session.InvoiceId), ["signed-proposal"], new PayjoinReceiverSessionStore.ProposalSettlement(transaction.GetHash().ToString(), 0, 123));
        Assert.Equal(initialVersion + 5, ReadVersion());
        store.AppendEvents(store.Owner(session.InvoiceId), ["normal-append"]);
        Assert.Equal(initialVersion + 6, ReadVersion());
        var proposal = Substitute.For<IPayjoinProposal>();
        proposal.Psbt().Returns(PSBT.FromTransaction(transaction, Network.RegTest).ToBase64());
        var nbx = new NBXplorerNetworkProvider(ChainName.Regtest);
        var networks = new BTCPayNetworkProvider([new BTCPayNetwork { CryptoCode = "BTC", NBXplorerNetwork = nbx.GetFromCryptoCode("BTC") }], nbx, new Logs());
        var finalizer = new PayjoinReceiverProposalFinalizer(null!, null!, bridges, store, networks);
        await finalizer.EnsureExpectedFinalTransactionAsync(new PayjoinReceiverProposalFinalizationContext(
            store.Owner(session.InvoiceId), session.StoreId, session.InvoiceId, "BTC"), proposal, CancellationToken.None);
        Assert.Equal(initialVersion + 6, ReadVersion());
        using (var legacy = testContext.CreateDbContext())
        {
            var bridge = legacy.AccountingBridges.Single();
            bridge.ExpectedFinalOutputIndex = null;
            bridge.ExpectedFinalValueSats = null;
            legacy.SaveChanges();
        }
        await finalizer.EnsureExpectedFinalTransactionAsync(new PayjoinReceiverProposalFinalizationContext(
            store.CreatePersister(store.GetAttempt(session.InvoiceId, session.SessionInstanceId)), session.StoreId, session.InvoiceId, "BTC"), proposal, CancellationToken.None);
        Assert.Equal(initialVersion + 7, ReadVersion());
        await bridges.MarkFailedAsync(session.InvoiceId, "manual review", CancellationToken.None);
        Assert.Equal(initialVersion + 8, ReadVersion());
        await bridges.MarkReconciledAsync(session.InvoiceId, null, null, null, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(initialVersion + 9, ReadVersion());

        using (var context = testContext.CreateDbContext())
        {
            var row = context.ReceiverSessions.Single(x => x.InvoiceId == session.InvoiceId);
            PayjoinReceiverSessionStore.RemoveAllSessionEvents(context, row);
            context.SaveChanges();
        }

        Assert.Equal(initialVersion + 10, ReadVersion());
    }

    private const string ReceiverAddress = "bcrt1qexampleaddress0000000000000000000000000";
    private const string PayjoinUri = "bitcoin:bcrt1qexample?amount=0.1&pjos=0&pj=https%3A%2F%2Fexample.com%2Fpj";

    private static PayjoinReceiverSessionState CreateSession(PayjoinReceiverSessionStore store, string invoiceId)
    {
        return store.GetOrCreateSession(
            invoiceId,
            "bcrt1qexampleaddress0000000000000000000000000",
            "store-1",
            DateTimeOffset.UtcNow.AddMinutes(15),
            ["bootstrap-event"]);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<int> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add(eventId.Id);
        }

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed class TestTransitionState : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
