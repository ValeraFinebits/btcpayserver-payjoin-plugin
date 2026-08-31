using BTCPayServer.Plugins.Payjoin.Services;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class PayjoinReceiverSessionStoreRelationalTests
{
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

    private static PayjoinReceiverSessionState CreateSession(PayjoinReceiverSessionStore store, string invoiceId)
    {
        return store.CreateSession(
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
}
