using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Npgsql;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests;

[Collection(typeof(NonParallelizableCollectionDefinition))]
public class PayjoinSeenInputConcurrencyIntegrationTests(ITestOutputHelper helper) : UnitTestBase(helper)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Integration", "Integration")]
    public async Task NativeSeenInputConflictRollsBackAndRejectsLosingSessionOnReplay(bool singleStatementBatches)
    {
        var timeout = TimeSpan.FromSeconds(30);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMinutes(2));
        using var senderKey = new Key();
        var senderAddress = senderKey.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        var freshInput = new OutPoint(uint256.Parse(new string('a', 64)), 0);
        var sharedInput = new OutPoint(uint256.Parse(new string('b', 64)), 1);
        var unrelatedInput = new OutPoint(uint256.Parse(new string('c', 64)), 2);
        var freshCoin = new Coin(freshInput, new TxOut(Money.Satoshis(40_000), senderAddress));
        var sharedCoin = new Coin(sharedInput, new TxOut(Money.Satoshis(100_000), senderAddress));
        const string losingInvoice = "invoice-native-seen-losing";
        const string winningInvoice = "invoice-native-seen-winning";
        var fixture = new PostgresSeenInputTestFixture(CreateDBTester(), singleStatementBatches,
            losingInvoice, freshInput, sharedInput, cancellation.Token);
        await using var fixtureScope = fixture.ConfigureAwait(true);
        await fixture.InitializeAsync(cancellation.Token);
        using var services = global::Payjoin.TestServices.Initialize();
        services.WaitForServicesReady();
        using var native = new NativeSeenInputTestSupport(services, fixture.Sessions);
        using var losingState = await native.PrepareAsync(losingInvoice, senderKey, [freshCoin, sharedCoin], cancellation.Token);
        using var winningState = await native.PrepareAsync(winningInvoice, senderKey, [sharedCoin], cancellation.Token);
        using (var seed = fixture.Factory.CreateContext())
        {
            seed.ReceiverSeenInputs.Add(new PayjoinReceiverSeenInputData
            {
                TransactionId = unrelatedInput.Hash.ToString(),
                OutputIndex = unrelatedInput.N,
                SeenAt = DateTimeOffset.UtcNow.AddDays(-1)
            });
            seed.SaveChanges();
        }
        var losingBefore = SnapshotSession(fixture.Factory, losingInvoice);
        var unrelatedBefore = SnapshotInputs(fixture.Factory);

        var guard = new ReplayingLosingSessionGuard(fixture.Sessions, losingInvoice);
        var dependencies = new UnexpectedReceiverDependencies();
        var bridge = new PayjoinAccountingBridgeService(fixture.Factory,
            new PostgresPayjoinUniqueConstraintViolationDetector(), new PayjoinSessionBuildLock());
        var mailroom = new PayjoinMailroomManager(NullLogger<PayjoinMailroomManager>.Instance, TimeSpan.FromMinutes(1),
            dependencies.FetchKeysAsync);
        var stateProcessor = new PayjoinReceiverStateProcessor(fixture.Sessions, dependencies, dependencies, fixture.SeenInputs, mailroom);
        using var log = new ProcessorLog();
        var processor = new PayjoinReceiverSessionProcessor(fixture.Sessions, guard, stateProcessor, dependencies, dependencies,
            bridge, dependencies, dependencies, dependencies, CreateNetworkProvider(ChainName.Regtest), log);

        fixture.Barrier.Arm();
        var losingTick = Task.Run(() => processor.ProcessTickAsync(cancellation.Token), cancellation.Token);
        SessionSnapshot? winningBefore = null;
        SeenInputSnapshot[] committedInputs = [];
        try
        {
            await fixture.Barrier.Reached.Task.WaitAsync(timeout, cancellation.Token);
            Assert.Equal(new[] { (freshInput.Hash.ToString(), (long)freshInput.N), (sharedInput.Hash.ToString(), (long)sharedInput.N) },
                fixture.Barrier.StagedInputs);
            RecordingSeenInputCallback? winningCallback = null;
            using var next = fixture.SeenInputs.ExecuteSeenInputTransition(winningInvoice, (callback, persister) =>
            {
                winningCallback = new RecordingSeenInputCallback(callback);
                using var transition = winningState.CheckNoInputsSeenBefore(winningCallback);
                return transition.Save(persister);
            });
            Assert.IsType<global::Payjoin.OutputsUnknown>(next);
            Assert.Equal(new[] { (sharedInput, false) }, Assert.IsType<RecordingSeenInputCallback>(winningCallback).Calls);
            AssertReplayedState<global::Payjoin.ReceiveSession.OutputsUnknown>(fixture.Sessions, winningInvoice);
            winningBefore = SnapshotSession(fixture.Factory, winningInvoice);
            committedInputs = SnapshotInputs(fixture.Factory);
        }
        finally
        {
            fixture.Barrier.Release();
            await losingTick.WaitAsync(timeout, CancellationToken.None);
        }

        var failure = Assert.Single(fixture.Barrier.Failures);
        var unique = Assert.IsType<PostgresException>(failure.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, unique.SqlState);
        Assert.Equal("IX_ReceiverSeenInputs_TransactionId_OutputIndex", unique.ConstraintName);
        Assert.IsType<global::Payjoin.ReceiverPersistedException.Storage>(Assert.Single(log.Exceptions));
        Assert.Equal(1, fixture.Sql.SharedInsertAttempts);
        Assert.Equal(1, guard.Replays);
        Assert.Empty(dependencies.Calls);

        var afterFailure = SnapshotInputs(fixture.Factory);
        Assert.DoesNotContain(afterFailure, row => row.TransactionId == freshInput.Hash.ToString());
        Assert.Equal(committedInputs, afterFailure);
        AssertSessionUnchanged(losingBefore, SnapshotSession(fixture.Factory, losingInvoice));
        AssertSessionUnchanged(Assert.IsType<SessionSnapshot>(winningBefore), SnapshotSession(fixture.Factory, winningInvoice));
        Assert.Equal(unrelatedBefore, afterFailure.Where(row => row.TransactionId == unrelatedInput.Hash.ToString()).ToArray());
        AssertReplayedState<global::Payjoin.ReceiveSession.MaybeInputsSeen>(fixture.Sessions, losingInvoice);
        Assert.True(fixture.Sql.ConflictHadTransaction);
        if (singleStatementBatches)
        {
            Assert.Null(fixture.Sql.ProbeFailure);
            Assert.True(fixture.Sql.ProbeHadTransaction);
            Assert.True(fixture.Sql.FreshInputExistedBeforeConflict, "The fresh row must exist in the working transaction before the shared INSERT.");
        }

        await processor.ProcessTickAsync(cancellation.Token).WaitAsync(timeout, cancellation.Token);
        Assert.Equal(2, guard.Replays);
        Assert.Single(fixture.Barrier.Failures);
        Assert.Equal(1, fixture.Sql.SharedInsertAttempts);
        Assert.Equal(2, log.Exceptions.Count);
        Assert.All(log.Exceptions, exception => Assert.IsType<global::Payjoin.ReceiverPersistedException.Storage>(exception));
        Assert.Empty(dependencies.Calls);
        AssertReplayedState<global::Payjoin.ReceiveSession.HasReplyableError>(fixture.Sessions, losingInvoice);
        var rejected = SnapshotSession(fixture.Factory, losingInvoice);
        Assert.Equivalent(losingBefore.Events, rejected.Events[..^1], strict: true);
        Assert.Contains("GotReplyableError", rejected.Events[^1].Event, StringComparison.Ordinal);
        Assert.DoesNotContain(rejected.Events, row => row.Event.Contains("CheckedNoInputsSeenBefore", StringComparison.Ordinal));
        Assert.DoesNotContain(rejected.Events, row => row.Event.Contains("FinalizedProposal", StringComparison.Ordinal));
        var finalInputs = SnapshotInputs(fixture.Factory);
        Assert.Single(finalInputs, row => row.TransactionId == freshInput.Hash.ToString() && row.OutputIndex == freshInput.N);
        Assert.Equal(committedInputs, finalInputs.Where(row => row.TransactionId != freshInput.Hash.ToString()).ToArray());
        AssertSessionUnchanged(winningBefore!, SnapshotSession(fixture.Factory, winningInvoice));
    }

    private static SessionSnapshot SnapshotSession(PayjoinPluginDbContextFactory factory, string invoiceId)
    {
        using var context = factory.CreateContext();
        return new SessionSnapshot(context.ReceiverSessions.AsNoTracking().Single(row => row.InvoiceId == invoiceId),
            context.ReceiverSessionEvents.AsNoTracking().Where(row => row.InvoiceId == invoiceId).OrderBy(row => row.Sequence).ToArray());
    }

    private static SeenInputSnapshot[] SnapshotInputs(PayjoinPluginDbContextFactory factory)
    {
        using var context = factory.CreateContext();
        return context.ReceiverSeenInputs.AsNoTracking().OrderBy(row => row.Id).AsEnumerable()
            .Select(row => new SeenInputSnapshot(row.Id, row.TransactionId, row.OutputIndex, row.SeenAt)).ToArray();
    }

    private static void AssertSessionUnchanged(SessionSnapshot expected, SessionSnapshot actual)
    {
        Assert.Equivalent(expected.Session, actual.Session, strict: true);
        Assert.Equivalent(expected.Events, actual.Events, strict: true);
    }

    private static void AssertReplayedState<T>(PayjoinReceiverSessionStore store, string invoiceId)
        where T : global::Payjoin.ReceiveSession
    {
        Assert.True(store.TryGetSession(invoiceId, out var session));
        using var replay = global::Payjoin.PayjoinMethods.ReplayReceiverEventLog(store.CreatePersister(session!));
        using var state = replay.State();
        Assert.IsType<T>(state);
    }

    private sealed record SessionSnapshot(PayjoinReceiverSessionData Session, PayjoinReceiverSessionEventData[] Events);
    private sealed record SeenInputSnapshot(long Id, string TransactionId, long OutputIndex, DateTimeOffset SeenAt);

    private sealed class ReplayingLosingSessionGuard(PayjoinReceiverSessionStore store, string losingInvoice) : IPayjoinReceiverSessionGuard
    {
        public int Replays { get; private set; }

        public Task<PayjoinReceiverSessionGuardResult?> TryPrepareAsync(PayjoinReceiverSessionState session, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (session.InvoiceId != losingInvoice)
            {
                return Task.FromResult<PayjoinReceiverSessionGuardResult?>(null);
            }
            var persister = store.CreatePersister(session);
            var replay = global::Payjoin.PayjoinMethods.ReplayReceiverEventLog(persister);
            Replays++;
            var script = BitcoinAddress.Create(session.ReceiverAddress, Network.RegTest).ScriptPubKey.ToBytes();
            return Task.FromResult<PayjoinReceiverSessionGuardResult?>(new PayjoinReceiverSessionGuardResult(
                session, persister, script, replay, replay.State(), _ => throw new InvalidOperationException("Unexpected session removal.")));
        }
    }

    private sealed class ProcessorLog : ILogger<PayjoinReceiverSessionProcessor>, IDisposable
    {
        public List<Exception> Exceptions { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
            {
                Exceptions.Add(exception);
            }
        }
        public void Dispose()
        {
            foreach (var exception in Exceptions.OfType<IDisposable>())
            {
                exception.Dispose();
            }
        }
    }
}
