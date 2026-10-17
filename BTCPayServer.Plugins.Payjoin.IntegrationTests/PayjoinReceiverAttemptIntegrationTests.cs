using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NBitcoin;
using Payjoin;
using Xunit;
using OutPoint = NBitcoin.OutPoint;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests;

public partial class PayjoinHistoryRetentionIntegrationTests
{
    [Theory]
    [InlineData("reservation", "session-read", false)]
    [InlineData("reservation", "bridge-checked", false)]
    [InlineData("proposal", "session-read", false)]
    [InlineData("proposal", "bridge-checked", false)]
    [InlineData("replacement", "session-read", false)]
    [InlineData("replacement", "bridge-checked", false)]
    [InlineData("reservation", "session-read", true)]
    [InlineData("reservation", "bridge-checked", true)]
    [InlineData("proposal", "session-read", true)]
    [InlineData("proposal", "bridge-checked", true)]
    [InlineData("replacement", "session-read", true)]
    [InlineData("replacement", "bridge-checked", true)]
    public async Task UnsignedCompletionCannotPartiallyDeleteAnAdvancedOrReplacementAttempt(string competing, string point, bool beforeOutputs)
    {
        var token = TestContext.Current.CancellationToken;
        var database = CreateDBTester();
        await database.MigrateAsync().WaitAsync(token).ConfigureAwait(true);
        try
        {
            var factory = new ObservedFactory(database.ConnectionString);
            using (var context = factory.CreateContext()) await context.Database.MigrateAsync(token).ConfigureAwait(true);
            using var services = TestServices.Initialize();
            services.WaitForServicesReady();
            using var key = new Key();
            var receiver = key.PubKey.WitHash.GetAddress(Network.RegTest);
            await PrepareUnderpaidNativeOriginalAsync(factory, services, receiver, token, 50_000).ConfigureAwait(true);
            var store = CreateStore(factory);
            Assert.True(store.TryGetSession(InvoiceId, out var original));
            Assert.NotNull(original);
            using (var replay = PayjoinMethods.ReplayReceiverEventLog(PayjoinReceiverSessionStore.Snapshot(original)))
            using (var state = replay.State())
            using (var history = replay.SessionHistory())
            using (var transition = Assert.IsType<ReceiveSession.WantsOutputs>(state).Inner.CommitOutputs())
            {
                var capture = new CapturingReceiverSessionPersister();
                using var wantsInputs = beforeOutputs ? null : transition.Save(capture);
                var tx = Transaction.Load(history.FallbackTx()!, Network.RegTest);
                var match = PayjoinReceiverPaymentValidation.ResolveFallbackReceiverOutput(tx, receiver.ScriptPubKey.ToBytes());
                var owner = store.CreatePersister(original);
                store.AttachOriginalPayment(owner, new PayjoinReceiverPaymentValidation.OriginalPayment(match.ValueSats!.Value, tx.GetHash().ToString(), match.OutputIndex!.Value));
                if (!beforeOutputs) store.CommitReceiverOutputs(owner, capture.Events,
                    new PayjoinReceiverOutputBuilder.OutputReplacement([], receiver.ScriptPubKey.ToBytes(), checked((ulong)match.ValueSats.Value), null));
            }
            var checkedState = store.GetAttempt(InvoiceId, original.SessionInstanceId);
            using var barrier = new WriteBarrier(point);
            var deletingFactory = new ObservedFactory(database.ConnectionString, barrier);
            var completion = Task.Run(() => CreateStore(deletingFactory).TryCompleteUnsignedAttempt(InvoiceId,
                checkedState.SessionInstanceId, checkedState.AttemptVersion, receiver.ScriptPubKey.ToBytes()), token);
            PayjoinReceiverSessionState expected;
            PayjoinAccountingBridgeState expectedBridge;
            try
            {
                await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20), token).ConfigureAwait(true);
                if (competing == "replacement")
                {
                    Assert.True(store.TryCompleteUnsignedAttempt(InvoiceId, checkedState.SessionInstanceId, checkedState.AttemptVersion, receiver.ScriptPubKey.ToBytes()));
                    using (var removed = factory.CreateContext())
                    {
                        Assert.Empty(removed.ReceiverSessions);
                        Assert.Empty(removed.ReceiverSessionEvents);
                        Assert.Empty(removed.AccountingBridges);
                        Assert.NotEmpty(removed.ReceiverSeenInputs);
                    }
                    var next = store.GetOrCreateSession(InvoiceId, receiver.ToString(), "store", checkedState.MonitoringExpiresAt,
                        Bootstrap(services, receiver.ToString(), 80_000), new CreatePayjoinAccountingBridgeRequest(InvoiceId, "store", "BTC", "BTC-CHAIN", checkedState.MonitoringExpiresAt));
                    store.StorePayjoinUri(InvoiceId, receiver.ToString(), "bitcoin:new-exact-uri?amount=0.0008", next.SessionInstanceId);
                    expected = store.GetAttempt(InvoiceId, next.SessionInstanceId);
                }
                else
                {
                    var outpoint = new OutPoint(uint256.Parse(new string('b', 64)), 0);
                    Assert.True(store.TryReserveContributedInput(store.CreatePersister(checkedState), "store", outpoint, checkedState.MonitoringExpiresAt));
                    if (competing == "proposal")
                    {
                        var persister = store.CreatePersister(store.GetAttempt(InvoiceId, checkedState.SessionInstanceId));
                        using var replay = PayjoinMethods.ReplayReceiverEventLog(persister);
                        using var state = replay.State();
                        using var outputs = state is ReceiveSession.WantsOutputs pending ? pending.Inner.CommitOutputs() : null;
                        var capture = new CapturingReceiverSessionPersister();
                        using var inputs = outputs?.Save(capture);
                        if (inputs is not null)
                            store.CommitReceiverOutputs(persister, capture.Events, new PayjoinReceiverOutputBuilder.OutputReplacement([], Convert.FromHexString(receiver.ScriptPubKey.ToHex()), 50_000, null));
                        using var receiverKey = new Key();
                        using var pair = InputPair.NewP2wpkh(new global::Payjoin.TxOut(60_000, receiverKey.PubKey.WitHash.ScriptPubKey.ToBytes()), new global::Payjoin.OutPoint(outpoint.Hash.ToString(), outpoint.N));
                        using var contributed = (inputs ?? Assert.IsType<ReceiveSession.WantsInputs>(state).Inner).ContributeInputs([pair]);
                        using var commit = contributed.CommitInputs();
                        using var fees = commit.Save(persister);
                        using var apply = fees.ApplyFeeRange(null, null);
                        using var provisional = apply.Save(persister);
                        using var finalize = provisional.FinalizeProposal(new NativeInputSigner(receiverKey, outpoint));
                        var relay = new StopBeforeNetwork();
                        await Assert.ThrowsAsync<NetworkStoppedException>(() => CreateFinalizer(store, CreateBridgeService(factory), relay).FinalizeCoreAsync(
                            new PayjoinReceiverProposalFinalizationContext(persister, "store", InvoiceId, "BTC"), finalize.Save, token)).ConfigureAwait(true);
                    }
                    expected = store.GetAttempt(InvoiceId, checkedState.SessionInstanceId);
                }
                expectedBridge = (await CreateBridgeService(factory).TryGetByInvoiceIdAsync(InvoiceId, token).ConfigureAwait(true))!;
            }
            finally { barrier.Release.TrySetResult(); }
            Assert.False(await completion.WaitAsync(token).ConfigureAwait(true));
            var saved = store.GetAttempt(InvoiceId, expected.SessionInstanceId);
            Assert.Equal(expected.PayjoinUri, saved.PayjoinUri);
            Assert.Equal(expected.GetEvents(), saved.GetEvents());
            Assert.Equal(expectedBridge, await CreateBridgeService(factory).TryGetByInvoiceIdAsync(InvoiceId, token).ConfigureAwait(true));
            using var verification = factory.CreateContext();
            Assert.Equal(competing == "replacement" ? 0 : 1, verification.ReceiverInputReservations.Count());
            Assert.NotEmpty(verification.ReceiverSeenInputs);
            Assert.Empty(factory.ProcessIds.Keys.Intersect(deletingFactory.ProcessIds.Keys));
        }
        finally
        {
            await using var core = database.CreateContext();
            await core.Database.EnsureDeletedAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    private sealed class NativeInputSigner(Key key, OutPoint outpoint) : ProcessPsbt
    {
        public string Callback(string psbt)
        {
            var parsed = PSBT.Parse(psbt, Network.RegTest);
            parsed.SignWithKeys(key);
            Assert.True(parsed.Inputs.Single(input => input.PrevOut == outpoint).TryFinalizeInput(out _));
            return parsed.ToBase64();
        }
    }

    [Fact]
    public async Task ReservationExpiryCannotDeleteAReplacementAttemptsReservation()
    {
        var token = TestContext.Current.CancellationToken;
        var database = CreateDBTester();
        await database.MigrateAsync().WaitAsync(token).ConfigureAwait(true);
        try
        {
            var factory = new ObservedFactory(database.ConnectionString);
            using (var context = factory.CreateContext()) await context.Database.MigrateAsync(token).ConfigureAwait(true);
            var store = CreateStore(factory);
            var old = store.GetOrCreateSession(InvoiceId, ReceiverAddress, "store", DateTimeOffset.UtcNow.AddHours(1), OriginalEvents);
            var outpoint = new OutPoint(uint256.One, 0);
            Assert.True(store.TryReserveContributedInput(store.CreatePersister(old), "store", outpoint, DateTimeOffset.UtcNow.AddMinutes(-1)));
            using var barrier = new WriteBarrier("bridge-checked");
            var cleanup = Task.Run(() => CreateStore(new ObservedFactory(database.ConnectionString, barrier)).CleanupExpiredInputReservations(DateTimeOffset.UtcNow), token);
            PayjoinReceiverSessionState next;
            try
            {
                await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20), token).ConfigureAwait(true);
                Assert.True(store.RemoveSession(InvoiceId, old.SessionInstanceId));
                next = store.GetOrCreateSession(InvoiceId, ReceiverAddress, "store", old.MonitoringExpiresAt, ["replacement bootstrap"]);
                Assert.True(store.TryReserveContributedInput(store.CreatePersister(next), "store", outpoint, DateTimeOffset.UtcNow.AddHours(1)));
            }
            finally { barrier.Release.TrySetResult(); }
            Assert.Equal(0, await cleanup.WaitAsync(token).ConfigureAwait(true));
            Assert.True(store.GetAttempt(InvoiceId, next.SessionInstanceId).TryGetContributedInput(out var retained));
            Assert.Equal(outpoint, retained);
            using var verification = factory.CreateContext();
            Assert.Single(verification.ReceiverInputReservations);
            Assert.Equal(["replacement bootstrap"], store.GetAttempt(InvoiceId, next.SessionInstanceId).GetEvents());
            Assert.False(factory.ProcessIds.IsEmpty);
        }
        finally
        {
            await using var core = database.CreateContext();
            await core.Database.EnsureDeletedAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task ABridgeOnlyFailureCannotAdoptASessionCreatedDuringItsSave()
    {
        var token = TestContext.Current.CancellationToken;
        var database = CreateDBTester();
        await database.MigrateAsync().WaitAsync(token).ConfigureAwait(true);
        try
        {
            var factory = new ObservedFactory(database.ConnectionString);
            using (var context = factory.CreateContext()) await context.Database.MigrateAsync(token).ConfigureAwait(true);
            var request = new CreatePayjoinAccountingBridgeRequest(InvoiceId, "store", "BTC", "BTC-CHAIN", DateTimeOffset.UtcNow.AddHours(1), EffectiveInvoiceValueSats: 123);
            await factory.SeedBridgeAsync(request, token).ConfigureAwait(true);
            using var services = TestServices.Initialize();
            services.WaitForServicesReady();
            using var key = new Key();
            var address = key.PubKey.WitHash.GetAddress(Network.RegTest).ToString();
            var events = Bootstrap(services, address, 50_000);
            using var barrier = new WriteBarrier("bridge-checked");
            var failed = Task.Run(() => CreateBridgeService(new ObservedFactory(database.ConnectionString, barrier)).MarkFailedAsync(InvoiceId, "stale bridge-only failure", token), token);
            PayjoinReceiverSessionState created;
            try
            {
                await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20), token).ConfigureAwait(true);
                created = CreateStore(factory).GetOrCreateSession(InvoiceId, address, "store", request.ExpiresAt!.Value, events, request);
            }
            finally { barrier.Release.TrySetResult(); }
            await Assert.ThrowsAsync<PayjoinSessionStaleException>(() => failed).ConfigureAwait(true);
            Assert.Equal(events, CreateStore(factory).GetAttempt(InvoiceId, created.SessionInstanceId).GetEvents());
            var bridge = await CreateBridgeService(factory).TryGetByInvoiceIdAsync(InvoiceId, token).ConfigureAwait(true);
            Assert.NotNull(bridge);
            Assert.Equal(50_000, bridge.EffectiveInvoiceValueSats);
            Assert.Equal(PayjoinAccountingBridgeStatus.PendingFallback, bridge.Status);
            Assert.Null(bridge.FailureMessage);
        }
        finally
        {
            await using var core = database.CreateContext();
            await core.Database.EnsureDeletedAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    [Theory]
    [InlineData("event")]
    [InlineData("reserve")]
    [InlineData("close")]
    [InlineData("failed")]
    [InlineData("outputs")]
    [InlineData("finalize")]
    [InlineData("delete")]
    [InlineData("discard")]
    [InlineData("uri")]
    [InlineData("consume")]
    [InlineData("fallback")]
    [InlineData("backfill")]
    public async Task LateWriteCannotCrossAttemptReplacementEvenAfterItReadTheOldRow(string operation)
    {
        var token = TestContext.Current.CancellationToken;
        var database = CreateDBTester();
        await database.MigrateAsync().WaitAsync(token).ConfigureAwait(true);
        try
        {
            var factory = new ObservedFactory(database.ConnectionString);
            using (var context = factory.CreateContext()) await context.Database.MigrateAsync(token).ConfigureAwait(true);
            var store = CreateStore(factory);
            var old = store.GetOrCreateSession(InvoiceId, ReceiverAddress, "store", DateTimeOffset.UtcNow.AddHours(1), OriginalEvents);
            await factory.SeedBridgeAsync(new CreatePayjoinAccountingBridgeRequest(InvoiceId, "store", "BTC", "BTC-CHAIN", old.MonitoringExpiresAt, EffectiveInvoiceValueSats: 50_000), token).ConfigureAwait(true);
            if (operation is "finalize" or "backfill")
            {
                store.CommitReceiverOutputs(store.CreatePersister(old), [], new PayjoinReceiverOutputBuilder.OutputReplacement([], Convert.FromHexString("51"), 50_000, null));
                old = store.GetAttempt(InvoiceId, old.SessionInstanceId);
            }
            if (operation == "consume") store.RequestClose(InvoiceId, InvoiceStatus.Expired, old.SessionInstanceId);
            using var barrier = new WriteBarrier("bridge-checked");
            var writer = new ObservedFactory(database.ConnectionString, barrier);
            var lateStore = CreateStore(writer);
            var relay = new StopBeforeNetwork();
            var late = Task.Run(async () =>
            {
                switch (operation)
                {
                    case "event": lateStore.CreatePersister(old).Save("late event"); break;
                    case "reserve": lateStore.TryReserveContributedInput(lateStore.CreatePersister(old), "store", new OutPoint(uint256.One, 0), old.MonitoringExpiresAt); break;
                    case "close": lateStore.RequestClose(InvoiceId, InvoiceStatus.Expired, old.SessionInstanceId); break;
                    case "failed": await CreateBridgeService(writer).MarkFailedAsync(InvoiceId, "late failure", token, old.SessionInstanceId).ConfigureAwait(false); break;
                    case "outputs": lateStore.CommitReceiverOutputs(lateStore.CreatePersister(old), ["late outputs"], new PayjoinReceiverOutputBuilder.OutputReplacement([], Convert.FromHexString("51"), 50_000, null)); break;
                    case "delete": lateStore.RemoveSession(InvoiceId, old.SessionInstanceId); break;
                    case "discard": lateStore.TryRemoveSessionUnlessNegotiating(InvoiceId, old.SessionInstanceId); break;
                    case "uri": lateStore.StorePayjoinUri(InvoiceId, ReceiverAddress, "old URI", old.SessionInstanceId); break;
                    case "consume": lateStore.TryConsumeInitializedPollAfterCloseRequest(InvoiceId, old.SessionInstanceId); break;
                    case "fallback": lateStore.AttachOriginalPayment(lateStore.CreatePersister(old), new PayjoinReceiverPaymentValidation.OriginalPayment(50_000, uint256.One.ToString(), 0)); break;
                    case "backfill":
                        await CreateFinalizer(lateStore, CreateBridgeService(writer), relay).EnsureExpectedFinalTransactionAsync(
                        new PayjoinReceiverProposalFinalizationContext(lateStore.CreatePersister(old), "store", InvoiceId, "BTC"), CreateProposal(out _), token).ConfigureAwait(false); break;
                    default:
                        var proposal = CreateProposal(out _);
                        await CreateFinalizer(lateStore, CreateBridgeService(writer), relay).FinalizeCoreAsync(
                            new PayjoinReceiverProposalFinalizationContext(lateStore.CreatePersister(old), "store", InvoiceId, "BTC"),
                            capture => { capture.Save("late signed proposal"); return proposal; }, token).ConfigureAwait(false);
                        break;
                }
            }, token);
            PayjoinReceiverSessionState replacement;
            try
            {
                await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20), token).ConfigureAwait(true);
                Assert.True(store.RemoveSession(InvoiceId, old.SessionInstanceId));
                replacement = store.GetOrCreateSession(InvoiceId, ReceiverAddress, "store", old.MonitoringExpiresAt, ["new bootstrap", "new event"]);
                store.StorePayjoinUri(InvoiceId, ReceiverAddress, "bitcoin:new?amount=0.0008&pj=https://example.com/new", replacement.SessionInstanceId);
                factory.SeedBridgeFields(InvoiceId, bridge =>
                {
                    bridge.EffectiveInvoiceValueSats = 80_000;
                    bridge.SettlementScript = null;
                });
            }
            finally { barrier.Release.TrySetResult(); }
            await Assert.ThrowsAsync<PayjoinSessionStaleException>(() => late).ConfigureAwait(true);
            var saved = store.GetAttempt(InvoiceId, replacement.SessionInstanceId);
            Assert.Equal(new[] { "new bootstrap", "new event" }, saved.GetEvents());
            Assert.Equal("bitcoin:new?amount=0.0008&pj=https://example.com/new", saved.PayjoinUri);
            Assert.False(saved.IsCloseRequested);
            Assert.False(saved.TryGetContributedInput(out _));
            using var verification = factory.CreateContext();
            var savedBridge = verification.AccountingBridges.Single();
            Assert.Equal(80_000, savedBridge.EffectiveInvoiceValueSats);
            Assert.Equal(PayjoinAccountingBridgeStatus.PendingFallback, savedBridge.Status);
            Assert.Null(savedBridge.SettlementScript);
            Assert.Null(savedBridge.ExpectedFinalTransactionId);
            Assert.Empty(verification.ReceiverInputReservations);
            Assert.Equal(0, relay.Calls);
            Assert.Empty(factory.ProcessIds.Keys.Intersect(writer.ProcessIds.Keys));
        }
        finally
        {
            await using var core = database.CreateContext();
            await core.Database.EnsureDeletedAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    [Theory]
    [InlineData("diagnostic")]
    [InlineData("poller")]
    [InlineData("payment")]
    public async Task ASessionReadNeverMixesItsRowWithEventsOfTheReplacement(string readerKind)
    {
        var token = TestContext.Current.CancellationToken;
        var database = CreateDBTester();
        await database.MigrateAsync().WaitAsync(token).ConfigureAwait(true);
        try
        {
            var factory = new ObservedFactory(database.ConnectionString);
            using (var context = factory.CreateContext()) await context.Database.MigrateAsync(token).ConfigureAwait(true);
            var store = CreateStore(factory);
            var old = store.GetOrCreateSession(InvoiceId, ReceiverAddress, "store", DateTimeOffset.UtcNow.AddHours(1), OriginalEvents);
            await factory.SeedBridgeAsync(new CreatePayjoinAccountingBridgeRequest(InvoiceId, "store", "BTC", "BTC-CHAIN",
                old.MonitoringExpiresAt, EffectiveInvoiceValueSats: 50_000), token).ConfigureAwait(true);
            using var barrier = new WriteBarrier("session-read");
            var reader = CreateStore(new ObservedFactory(database.ConnectionString, barrier));
            PayjoinAccountingBridgeState? paymentBridge = null;
            var reading = Task.Run(() => readerKind switch
            {
                "poller" => Assert.Single(reader.GetSessions()),
                "payment" => ReadPayment(),
                _ => Read()
            }, token);
            PayjoinReceiverSessionState ReadPayment()
            {
                var payment = reader.ReadPaymentSnapshot(reader.CreatePersister(old));
                paymentBridge = payment.Bridge;
                return payment.Session;
            }
            PayjoinReceiverSessionState Read()
            {
                Assert.True(reader.TryGetSession(InvoiceId, out var state));
                return Assert.IsType<PayjoinReceiverSessionState>(state);
            }
            try
            {
                var first = await Task.WhenAny(barrier.Entered.Task, reading).WaitAsync(TimeSpan.FromSeconds(20), token).ConfigureAwait(true);
                if (first == reading) await reading.ConfigureAwait(true);
                await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20), token).ConfigureAwait(true);
                Assert.True(store.RemoveSession(InvoiceId, old.SessionInstanceId));
                store.GetOrCreateSession(InvoiceId, ReceiverAddress, "store", old.MonitoringExpiresAt, ["new events"]);
                factory.SeedBridgeFields(InvoiceId, bridge => bridge.EffectiveInvoiceValueSats = 80_000);
            }
            finally { barrier.Release.TrySetResult(); }
            var snapshot = await reading.WaitAsync(token).ConfigureAwait(true);
            Assert.Equal(old.SessionInstanceId, snapshot.SessionInstanceId);
            Assert.Equal(OriginalEvents, snapshot.GetEvents());
            if (readerKind == "payment") Assert.Equal(50_000, paymentBridge!.EffectiveInvoiceValueSats);
            Assert.Throws<PayjoinSessionStaleException>(() => reader.CreatePersister(old).Load());
        }
        finally
        {
            await using var core = database.CreateContext();
            await core.Database.EnsureDeletedAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeBootstrapAndBridgeAreAtomicAndTheWinningPinIsImmutable(bool interrupt)
    {
        var token = TestContext.Current.CancellationToken;
        var database = CreateDBTester();
        await database.MigrateAsync().WaitAsync(token).ConfigureAwait(true);
        try
        {
            var factory = new ObservedFactory(database.ConnectionString);
            using (var context = factory.CreateContext()) await context.Database.MigrateAsync(token).ConfigureAwait(true);
            using var services = TestServices.Initialize();
            services.WaitForServicesReady();
            using var key = new Key();
            var address = key.PubKey.WitHash.GetAddress(Network.RegTest).ToString();
            var firstEvents = Bootstrap(services, address, 50_000);
            var secondEvents = Bootstrap(services, address, 80_000);
            var request = new CreatePayjoinAccountingBridgeRequest(InvoiceId, "store", "BTC", "BTC-CHAIN", DateTimeOffset.UtcNow.AddHours(1));
            await factory.SeedBridgeAsync(request with { EffectiveInvoiceValueSats = 123 }, token).ConfigureAwait(true);
            if (interrupt)
            {
                factory.FailNextWrite = true;
                Assert.Throws<DbUpdateException>(() => CreateStore(factory).GetOrCreateSession(InvoiceId, address, "store", request.ExpiresAt!.Value, firstEvents, request));
                using var context = factory.CreateContext();
                Assert.Empty(context.ReceiverSessions);
                Assert.Empty(context.ReceiverSessionEvents);
                Assert.Equal(123, context.AccountingBridges.Single().EffectiveInvoiceValueSats);
            }
            using var firstBarrier = new WriteBarrier("session-read");
            using var secondBarrier = new WriteBarrier("session-read");
            var first = Task.Run(() => CreateStore(new ObservedFactory(database.ConnectionString, firstBarrier)).GetOrCreateSession(InvoiceId, address, "store", request.ExpiresAt!.Value, firstEvents, request), token);
            var second = Task.Run(() => CreateStore(new ObservedFactory(database.ConnectionString, secondBarrier)).GetOrCreateSession(InvoiceId, address, "store", request.ExpiresAt!.Value, secondEvents, request), token);
            PayjoinReceiverSessionState winner;
            try
            {
                await Task.WhenAll(firstBarrier.Entered.Task, secondBarrier.Entered.Task).WaitAsync(TimeSpan.FromSeconds(20), token).ConfigureAwait(true);
                firstBarrier.Release.TrySetResult();
                winner = await first.WaitAsync(token).ConfigureAwait(true);
            }
            finally { firstBarrier.Release.TrySetResult(); secondBarrier.Release.TrySetResult(); }
            var loser = await second.WaitAsync(token).ConfigureAwait(true);
            Assert.Equal(winner.SessionInstanceId, loser.SessionInstanceId);
            Assert.Equal(firstEvents, winner.GetEvents());
            Assert.Equal(firstEvents, loser.GetEvents());
            using var verification = factory.CreateContext();
            Assert.Equal(50_000, verification.AccountingBridges.Single().EffectiveInvoiceValueSats);
            Assert.Equal(winner.SessionInstanceId, CreateStore(factory).GetAttempt(InvoiceId, winner.SessionInstanceId).SessionInstanceId);
        }
        finally
        {
            await using var core = database.CreateContext();
            await core.Database.EnsureDeletedAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    private static string[] Bootstrap(TestServices services, string address, ulong pin)
    {
        using var keys = services.FetchOhttpKeys();
        using var builder = new ReceiverBuilder(address, services.DirectoryUrl(), keys);
        using var pinned = builder.WithAmount(pin);
        using var transition = pinned.Build();
        var capture = new CapturingReceiverSessionPersister();
        using var initialized = transition.Save(capture);
        return capture.Load();
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("outputs")]
    [InlineData("delete")]
    public async Task EarlyNativeRejectionIsAtomicAgainstPreparationAndFailure(string competing)
    {
        var token = TestContext.Current.CancellationToken;
        var database = CreateDBTester();
        await database.MigrateAsync().WaitAsync(token).ConfigureAwait(true);
        try
        {
            var factory = new ObservedFactory(database.ConnectionString);
            using (var context = factory.CreateContext()) await context.Database.MigrateAsync(token).ConfigureAwait(true);
            using var services = TestServices.Initialize();
            services.WaitForServicesReady();
            using var receiverKey = new Key();
            var receiver = receiverKey.PubKey.WitHash.GetAddress(Network.RegTest);
            await PrepareUnderpaidNativeOriginalAsync(factory, services, receiver, token).ConfigureAwait(true);
            var store = CreateStore(factory);
            Assert.True(store.TryGetSession(InvoiceId, out var checkedState));
            Assert.NotNull(checkedState);
            var originalEvents = checkedState.GetEvents();
            var script = receiver.ScriptPubKey.ToBytes();
            if (competing == "delete")
            {
                Assert.Equal(PayjoinReceiverSessionStore.EarlyRejectionResult.Rejected, store.TryRejectEarlyOriginal(checkedState, script));
                using var context = factory.CreateContext();
                Assert.Empty(context.ReceiverSessions);
                Assert.Empty(context.ReceiverSessionEvents);
                Assert.Empty(context.AccountingBridges);
                Assert.Empty(context.ReceiverInputReservations);
                Assert.NotEmpty(context.ReceiverSeenInputs);
                var next = store.GetOrCreateSession(InvoiceId, receiver.ToString(), "store", checkedState.MonitoringExpiresAt,
                    Bootstrap(services, receiver.ToString(), 80_000), new CreatePayjoinAccountingBridgeRequest(InvoiceId, "store", "BTC", "BTC-CHAIN", checkedState.MonitoringExpiresAt));
                Assert.NotEqual(checkedState.SessionInstanceId, next.SessionInstanceId);
                Assert.Throws<PayjoinSessionStaleException>(() => store.CreatePersister(checkedState).Save("late event"));
                Assert.Equal(80_000, (await CreateBridgeService(factory).TryGetByInvoiceIdAsync(InvoiceId, token).ConfigureAwait(true))!.EffectiveInvoiceValueSats);
                return;
            }
            using var barrier = new WriteBarrier("bridge-checked");
            var deleting = CreateStore(new ObservedFactory(database.ConnectionString, barrier));
            var rejection = Task.Run(() => deleting.TryRejectEarlyOriginal(checkedState, script), token);
            string[] expectedEvents;
            try
            {
                await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20), token).ConfigureAwait(true);
                if (competing == "failed")
                    await CreateBridgeService(factory).MarkFailedAsync(InvoiceId, "independent recovery reason", token, checkedState.SessionInstanceId).ConfigureAwait(true);
                else
                {
                    using var replay = PayjoinMethods.ReplayReceiverEventLog(PayjoinReceiverSessionStore.Snapshot(checkedState));
                    using var state = replay.State();
                    using var transition = Assert.IsType<ReceiveSession.WantsOutputs>(state).Inner.CommitOutputs();
                    var capture = new CapturingReceiverSessionPersister();
                    using var wantsInputs = transition.Save(capture);
                    store.CommitReceiverOutputs(store.CreatePersister(checkedState), capture.Events, new PayjoinReceiverOutputBuilder.OutputReplacement([], Convert.FromHexString(receiver.ScriptPubKey.ToHex()), 50_000, null));
                }
                expectedEvents = store.GetAttempt(InvoiceId, checkedState.SessionInstanceId).GetEvents();
            }
            finally { barrier.Release.TrySetResult(); }
            Assert.Equal(PayjoinReceiverSessionStore.EarlyRejectionResult.StaleOrAdvanced, await rejection.WaitAsync(token).ConfigureAwait(true));
            var saved = store.GetAttempt(InvoiceId, checkedState.SessionInstanceId);
            Assert.Equal(checkedState.PayjoinUri, saved.PayjoinUri);
            Assert.Equal(expectedEvents, saved.GetEvents());
            Assert.Equal(originalEvents, saved.GetEvents()[..originalEvents.Length]);
            var savedBridge = await CreateBridgeService(factory).TryGetByInvoiceIdAsync(InvoiceId, token).ConfigureAwait(true);
            Assert.NotNull(savedBridge);
            if (competing == "failed")
            {
                Assert.Equal(PayjoinAccountingBridgeStatus.Failed, savedBridge.Status);
                Assert.Equal("independent recovery reason", savedBridge.FailureMessage);
            }
            else Assert.Equal(Convert.ToHexString(receiver.ScriptPubKey.ToBytes()), savedBridge.SettlementScript);
        }
        finally
        {
            await using var core = database.CreateContext();
            await core.Database.EnsureDeletedAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    private static async Task PrepareUnderpaidNativeOriginalAsync(ObservedFactory factory, TestServices services, BitcoinAddress receiver, CancellationToken token, long amountSats = 40_000)
    {
        var store = CreateStore(factory);
        var events = Bootstrap(services, receiver.ToString(), 50_000);
        var session = store.GetOrCreateSession(InvoiceId, receiver.ToString(), "store", DateTimeOffset.UtcNow.AddHours(1), events,
            new CreatePayjoinAccountingBridgeRequest(InvoiceId, "store", "BTC", "BTC-CHAIN", DateTimeOffset.UtcNow.AddHours(1)));
        var persister = store.CreatePersister(session);
        using var replay = PayjoinMethods.ReplayReceiverEventLog(persister);
        using var initial = replay.State();
        var initialized = Assert.IsType<ReceiveSession.Initialized>(initial).Inner;
        using var uri = initialized.PjUri();
        store.StorePayjoinUri(InvoiceId, receiver.ToString(), uri.AsString(), session.SessionInstanceId);
        using var senderKey = new Key();
        var address = senderKey.PubKey.WitHash.GetAddress(Network.RegTest);
        var coin = new Coin(new OutPoint(uint256.One, 0), new NBitcoin.TxOut(Money.Satoshis(100_000), address));
        var psbt = Network.RegTest.CreateTransactionBuilder().AddCoins(coin).AddKeys(senderKey).Send(receiver, Money.Satoshis(amountSats))
            .SetChange(address).SendFees(Money.Satoshis(1000)).BuildPSBT(true).Finalize();
        using var senderUri = uri.SetAmountSats(checked((ulong)amountSats));
        using var builder = new SenderBuilder(psbt.ToBase64(), senderUri);
        using var senderTransition = builder.BuildRecommended(1000);
        var senderLog = new SenderLog();
        using var sender = senderTransition.Save(senderLog);
        using var post = sender.CreateV2PostRequest(services.OhttpRelayUrl());
        using var postResponse = sender.ProcessResponse(await NativeRequestAsync(post.Request, token).ConfigureAwait(false), post.OhttpCtx);
        using var polling = postResponse.Save(senderLog);
        using var poll = initialized.CreatePollRequest(services.OhttpRelayUrl());
        using var pollResponse = initialized.ProcessResponse(await NativeRequestAsync(poll.Request, token).ConfigureAwait(false), poll.ClientResponse);
        using var outcome = pollResponse.Save(persister);
        using var interactive = Assert.IsType<InitializedTransitionOutcome.Progress>(outcome).Inner.AssumeInteractiveReceiver();
        using var owned = interactive.Save(persister);
        using var ownership = owned.CheckInputsNotOwned(new ForeignInputs());
        using var seen = ownership.Save(persister);
        using var outputs = new PayjoinSeenInputStore(factory).ExecuteSeenInputTransition(persister, (callback, writer) =>
        {
            using var history = seen.CheckNoInputsSeenBefore(callback);
            return history.Save(writer);
        });
        using var identify = outputs.IdentifyReceiverOutputs(new ReceiverScript(receiver.ScriptPubKey.ToBytes()));
        using var wantsOutputs = identify.Save(persister);
    }

    private static async Task<byte[]> NativeRequestAsync(Request request, CancellationToken token)
    {
        using var client = new HttpClient();
        using var content = new ByteArrayContent(request.Body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(request.ContentType);
        using var response = await client.PostAsync(new System.Uri(request.Url), content, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
    }

    private sealed class SenderLog : JsonSenderSessionPersister
    {
        private readonly List<string> _events = [];
        public string[] Load() => _events.ToArray();
        public void Save(string @event) => _events.Add(@event);
        public void Close() { }
    }
    private sealed class ForeignInputs : IsInputOwned { public bool Callback(global::Payjoin.OutPoint outpoint) => false; }
    private sealed class ReceiverScript(byte[] script) : IsScriptOwned { public bool Callback(byte[] candidate) => script.AsSpan().SequenceEqual(candidate); }

    [Fact]
    public async Task InstanceMigrationPreservesLegacyHistoryAndAssignsStableDistinctIds()
    {
        var token = TestContext.Current.CancellationToken;
        var database = CreateDBTester();
        await database.MigrateAsync().WaitAsync(token).ConfigureAwait(true);
        try
        {
            var factory = new ObservedFactory(database.ConnectionString);
            using (var context = factory.CreateContext())
            {
                var previous = context.Database.GetMigrations().Last(x => !x.EndsWith("AddReceiverSessionInstanceId", StringComparison.Ordinal));
                await context.GetService<IMigrator>().MigrateAsync(previous, token).ConfigureAwait(true);
                await context.Database.ExecuteSqlRawAsync("""
                    INSERT INTO "BTCPayServer.Plugins.Payjoin"."ReceiverSessions"
                    ("InvoiceId","StoreId","ReceiverAddress","MonitoringExpiresAt","CreatedAt","UpdatedAt","IsCloseRequested","InitializedPollAfterCloseRequestConsumed","DestructiveWriteStamp","PayjoinUri")
                    VALUES ('legacy-1','store','receiver',NOW()+INTERVAL '1 hour',NOW(),NOW(),false,false,7,'bitcoin:legacy?amount=0.1'),
                           ('legacy-2','store','receiver',NOW()+INTERVAL '1 hour',NOW(),NOW(),false,false,8,'bitcoin:other?amount=0.2');
                    INSERT INTO "BTCPayServer.Plugins.Payjoin"."ReceiverSessionEvents" ("InvoiceId","Sequence","Event","CreatedAt")
                    VALUES ('legacy-1',1,'legacy original',NOW()),('legacy-1',2,'legacy signed proposal',NOW());
                    """, token).ConfigureAwait(true);
                await context.Database.MigrateAsync(token).ConfigureAwait(true);
            }
            var store = CreateStore(factory);
            Assert.True(store.TryGetSession("legacy-1", out var first));
            Assert.True(store.TryGetSession("legacy-2", out var second));
            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.NotEqual(Guid.Empty, first.SessionInstanceId);
            Assert.NotEqual(first.SessionInstanceId, second.SessionInstanceId);
            Assert.Equal("bitcoin:legacy?amount=0.1", first.PayjoinUri);
            Assert.Equal(new[] { "legacy original", "legacy signed proposal" }, first.GetEvents());
            Assert.Equal(7, first.AttemptVersion);
            Assert.Equal(first.SessionInstanceId, CreateStore(factory).GetAttempt("legacy-1", first.SessionInstanceId).SessionInstanceId);
        }
        finally
        {
            await using var core = database.CreateContext();
            await core.Database.EnsureDeletedAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }
}
