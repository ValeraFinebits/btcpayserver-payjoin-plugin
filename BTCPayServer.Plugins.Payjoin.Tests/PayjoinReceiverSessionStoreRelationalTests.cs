using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class PayjoinReceiverSessionStoreRelationalTests
{
    private const int ConcurrencyRetryEventId = 1;
    private const int ConcurrencyExhaustedEventId = 2;

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
            store.ExecuteSeenInputTransition<TestTransitionState>(
                session.InvoiceId,
                (callback, _) =>
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
        using var retryState = store.ExecuteSeenInputTransition<TestTransitionState>(
            session.InvoiceId,
            (callback, persister) =>
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

        using var firstState = store.ExecuteSeenInputTransition<TestTransitionState>(
            firstSession.InvoiceId,
            (callback, persister) =>
            {
                Assert.False(callback.Callback(input));
                persister.Save("first-session-checked-inputs");
                return new TestTransitionState();
            });

        var wasSeenBySecondSession = false;
        using var secondState = store.ExecuteSeenInputTransition<TestTransitionState>(
            secondSession.InvoiceId,
            (callback, persister) =>
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
            store.ExecuteSeenInputTransition<TestTransitionState>(
                session.InvoiceId,
                (callback, persister) =>
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
            store.ExecuteSeenInputTransition<TestTransitionState>(
                session.InvoiceId,
                (callback, persister) =>
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
        Assert.True(firstStore.TryReserveContributedInput(firstSession.StoreId, firstSession.InvoiceId, outPoint, expiresAt));
        Assert.False(secondStore.TryReserveContributedInput(secondSession.StoreId, secondSession.InvoiceId, outPoint, expiresAt));

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
            return firstStore.TryReserveContributedInput(firstSession.StoreId, firstSession.InvoiceId, outPoint, expiresAt);
        });
        var secondAttempt = Task.Run(() =>
        {
            start.Wait();
            return secondStore.TryReserveContributedInput(secondSession.StoreId, secondSession.InvoiceId, outPoint, expiresAt);
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
    public void AppendEventsWithAccountingUpdateWritesEventsAndBridgeTogether()
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
                Status = Data.PayjoinAccountingBridgeStatus.PendingFallback,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            seed.SaveChanges();
        }

        store.AppendEventsWithAccountingUpdate(
            session.InvoiceId,
            ["event-a", "event-b"],
            bridge =>
            {
                bridge.SettlementScript = "AABB";
                bridge.EffectiveInvoiceValueSats = 1234;
            });

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
    public void AppendEventsWithAccountingUpdateAppendsEventsWhenNoBridgeExists()
    {
        using var testContext = new RelationalPluginTestContext();
        var store = testContext.CreateStore();
        var session = CreateSession(store, "invoice-atomic-no-bridge");

        store.AppendEventsWithAccountingUpdate(session.InvoiceId, ["event-a"], bridge => bridge.SettlementScript = "AABB");

        using var context = testContext.CreateDbContext();
        var events = context.ReceiverSessionEvents
            .Where(x => x.InvoiceId == session.InvoiceId)
            .OrderBy(x => x.Sequence)
            .Select(x => x.Event)
            .ToArray();
        Assert.Equal(new[] { "bootstrap-event", "event-a" }, events);
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
        Assert.True(store.TryReserveContributedInput(session.StoreId, session.InvoiceId, outPoint, DateTimeOffset.UtcNow.AddMinutes(10)));

        Assert.False(store.TryRemoveSessionUnlessNegotiating(session.InvoiceId));
        Assert.True(store.TryGetSession(session.InvoiceId, out _));
        using var context = testContext.CreateDbContext();
        Assert.True(context.ReceiverInputReservations.Any(x => x.InvoiceId == session.InvoiceId));
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

        Assert.True(store.TryReserveContributedInput(session.StoreId, session.InvoiceId, outPoint, DateTimeOffset.UtcNow.AddMinutes(10)));

        Assert.True(store.RequestClose(session.InvoiceId, InvoiceStatus.Expired));
        Assert.True(store.TryConsumeInitializedPollAfterCloseRequest(session.InvoiceId));
        store.AppendEventsWithAccountingUpdate(session.InvoiceId, ["event-after-reservation"], updateBridge: null);
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
        Assert.True(store.TryReserveContributedInput(session.StoreId, session.InvoiceId, outPoint, DateTimeOffset.UtcNow.AddMinutes(10)));

        Assert.Null(staleRow.ContributedInputTransactionId);
        PayjoinReceiverSessionStore.RemoveAllSessionEvents(readContext, staleRow);
        readContext.ReceiverSessions.Remove(staleRow);
        Assert.Throws<DbUpdateConcurrencyException>(() => readContext.SaveChanges());

        Assert.True(store.TryGetSession(session.InvoiceId, out _));
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

    private sealed class TestTransitionState : IDisposable
    {
        public void Dispose()
        {
        }
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
}
