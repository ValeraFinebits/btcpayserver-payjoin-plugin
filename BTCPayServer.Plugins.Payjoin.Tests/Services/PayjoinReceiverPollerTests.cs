using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client.Models;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Wallets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBXplorer;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using NSubstitute;
using Payjoin;
using Xunit;
using ReceiveSessionState = global::Payjoin.ReceiveSession;
using SystemUri = System.Uri;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinReceiverPollerTests
{
    [Fact]
    public void TryExpireSessionRemovesExpiredSession()
    {
        // Arrange
        using var context = new TestContext();
        var sessionStore = context.CreateStore();
        using var poller = CreatePoller(sessionStore);
        var session = sessionStore.CreateSession(
            "invoice-expired",
            "bcrt1qexampleaddress0000000000000000000000000",
            "store-1",
            new SystemUri("https://relay.example/"),
            DateTimeOffset.UtcNow.AddMinutes(-1),
            out _);

        // Act
        var expired = poller.TryExpireSession(session);

        // Assert
        Assert.True(expired);
        Assert.False(sessionStore.TryGetSession(session.InvoiceId, out _));
    }

    [Fact]
    public void TryExpireSessionKeepsActiveSession()
    {
        // Arrange
        using var context = new TestContext();
        var sessionStore = context.CreateStore();
        using var poller = CreatePoller(sessionStore);
        var session = sessionStore.CreateSession(
            "invoice-active",
            "bcrt1qexampleaddress0000000000000000000000000",
            "store-1",
            new SystemUri("https://relay.example/"),
            DateTimeOffset.UtcNow.AddMinutes(10),
            out _);

        // Act
        var expired = poller.TryExpireSession(session);

        // Assert
        Assert.False(expired);
        Assert.True(sessionStore.TryGetSession(session.InvoiceId, out _));
    }

    [Fact]
    public void TryRemoveCloseRequestedSessionReturnsFalseWhenCloseNotRequested()
    {
        // Arrange
        using var context = new TestContext();
        var sessionStore = context.CreateStore();
        using var poller = CreatePoller(sessionStore);
        var session = sessionStore.CreateSession(
            "invoice-open",
            "bcrt1qexampleaddress0000000000000000000000000",
            "store-1",
            new SystemUri("https://relay.example/"),
            DateTimeOffset.UtcNow.AddMinutes(10),
            out _);
        using var state = CreateMonitorState();

        // Act
        var removed = poller.TryRemoveCloseRequestedSession(session, state);

        // Assert
        Assert.False(removed);
        Assert.True(sessionStore.TryGetSession(session.InvoiceId, out _));
    }

    [Fact]
    public void TryRemoveCloseRequestedSessionKeepsSessionWhenStateHasReplyableError()
    {
        // Arrange
        using var context = new TestContext();
        var sessionStore = context.CreateStore();
        using var poller = CreatePoller(sessionStore);
        var session = sessionStore.CreateSession(
            "invoice-close-replyable-error",
            "bcrt1qexampleaddress0000000000000000000000000",
            "store-1",
            new SystemUri("https://relay.example/"),
            DateTimeOffset.UtcNow.AddMinutes(10),
            out _);
        Assert.True(sessionStore.RequestClose(session.InvoiceId, InvoiceStatus.Expired));
        Assert.True(sessionStore.TryGetSession(session.InvoiceId, out var closeRequested));
        using var state = CreateHasReplyableErrorState();

        // Act
        var removed = poller.TryRemoveCloseRequestedSession(closeRequested!, state);

        // Assert
        Assert.False(removed);
        Assert.True(sessionStore.TryGetSession(session.InvoiceId, out _));
    }

    [Fact]
    public void TryRemoveCloseRequestedSessionKeepsSessionWhenInitializedPollAfterCloseRequestNotConsumed()
    {
        // Arrange
        using var context = new TestContext();
        var sessionStore = context.CreateStore();
        using var poller = CreatePoller(sessionStore);
        var session = sessionStore.CreateSession(
            "invoice-close-initialized-keep",
            "bcrt1qexampleaddress0000000000000000000000000",
            "store-1",
            new SystemUri("https://relay.example/"),
            DateTimeOffset.UtcNow.AddMinutes(10),
            out _);
        Assert.True(sessionStore.RequestClose(session.InvoiceId, InvoiceStatus.Expired));
        Assert.True(sessionStore.TryGetSession(session.InvoiceId, out var closeRequested));
        using var state = CreateInitializedState();

        // Act
        var removed = poller.TryRemoveCloseRequestedSession(closeRequested!, state);

        // Assert
        Assert.False(removed);
        Assert.True(sessionStore.TryGetSession(session.InvoiceId, out _));
    }

    [Fact]
    public void TryRemoveCloseRequestedSessionRemovesSessionWhenInitializedPollAfterCloseRequestConsumed()
    {
        // Arrange
        using var context = new TestContext();
        var sessionStore = context.CreateStore();
        using var poller = CreatePoller(sessionStore);
        var session = sessionStore.CreateSession(
            "invoice-close-initialized-remove",
            "bcrt1qexampleaddress0000000000000000000000000",
            "store-1",
            new SystemUri("https://relay.example/"),
            DateTimeOffset.UtcNow.AddMinutes(10),
            out _);
        Assert.True(sessionStore.RequestClose(session.InvoiceId, InvoiceStatus.Expired));
        Assert.True(sessionStore.TryConsumeInitializedPollAfterCloseRequest(session.InvoiceId));
        Assert.True(sessionStore.TryGetSession(session.InvoiceId, out var closeRequested));
        using var state = CreateInitializedState();

        // Act
        var removed = poller.TryRemoveCloseRequestedSession(closeRequested!, state);

        // Assert
        Assert.True(removed);
        Assert.False(sessionStore.TryGetSession(session.InvoiceId, out _));
    }

    [Fact]
    public void TryRemoveCloseRequestedSessionRemovesSessionWhenStateCannotReply()
    {
        // Arrange
        using var context = new TestContext();
        var sessionStore = context.CreateStore();
        using var poller = CreatePoller(sessionStore);
        var session = sessionStore.CreateSession(
            "invoice-close-remove",
            "bcrt1qexampleaddress0000000000000000000000000",
            "store-1",
            new SystemUri("https://relay.example/"),
            DateTimeOffset.UtcNow.AddMinutes(10),
            out _);
        Assert.True(sessionStore.RequestClose(session.InvoiceId, InvoiceStatus.Expired));
        Assert.True(sessionStore.TryGetSession(session.InvoiceId, out var closeRequested));
        using var state = CreateMonitorState();

        // Act
        var removed = poller.TryRemoveCloseRequestedSession(closeRequested!, state);

        // Assert
        Assert.True(removed);
        Assert.False(sessionStore.TryGetSession(session.InvoiceId, out _));
    }

    [Fact]
    public void TryRemoveCloseRequestedSessionKeepsSessionWhenStateCanReply()
    {
        // Arrange
        using var context = new TestContext();
        var sessionStore = context.CreateStore();
        using var poller = CreatePoller(sessionStore);
        var session = sessionStore.CreateSession(
            "invoice-close-keep",
            "bcrt1qexampleaddress0000000000000000000000000",
            "store-1",
            new SystemUri("https://relay.example/"),
            DateTimeOffset.UtcNow.AddMinutes(10),
            out _);
        Assert.True(sessionStore.RequestClose(session.InvoiceId, InvoiceStatus.Expired));
        Assert.True(sessionStore.TryGetSession(session.InvoiceId, out var closeRequested));
        using var state = CreateUncheckedOriginalPayloadState();

        // Act
        var removed = poller.TryRemoveCloseRequestedSession(closeRequested!, state);

        // Assert
        Assert.False(removed);
        Assert.True(sessionStore.TryGetSession(session.InvoiceId, out _));
    }

    [Fact]
    public void CreateExactPaymentReceiverOutputsCreatesDedicatedChangeOutput()
    {
        // Arrange
        var receiverScript = new byte[] { 0x01, 0x02 };
        var receiverChangeScript = new byte[] { 0xAA, 0xBB };

        // Act
        var result = PayjoinReceiverPoller.CreateExactPaymentReceiverOutputs(
            50_000UL,
            receiverScript,
            receiverChangeScript);

        // Assert
        Assert.Equal(receiverChangeScript, result.ReceiverChangeScript);
        Assert.Equal(2, result.ExactPaymentOutputs.Length);
        Assert.Equal<ulong>(50_000UL, result.ExactPaymentOutputs[0].valueSat);
        Assert.Equal(receiverScript, result.ExactPaymentOutputs[0].scriptPubkey);
        Assert.Equal<ulong>(0UL, result.ExactPaymentOutputs[1].valueSat);
        Assert.Equal(receiverChangeScript, result.ExactPaymentOutputs[1].scriptPubkey);
    }

    [Fact]
    public void EnsureContributedInputsPresentSucceedsWhenAllContributedInputsExist()
    {
        // Arrange
        var outPoint = new OutPoint(uint256.Parse("1111111111111111111111111111111111111111111111111111111111111111"), 0);
        var psbt = CreatePsbtWithInputs(outPoint);
        var receivedCoins = new[] { CreateReceivedCoin(outPoint, Money.Satoshis(50_000), CreateScript(1)) };

        // Act
        var exception = Record.Exception(() => PayjoinReceiverPoller.EnsureContributedInputsPresent(psbt, receivedCoins));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void EnsureContributedInputsPresentThrowsWhenInputMissing()
    {
        // Arrange
        var missingOutPoint = new OutPoint(uint256.Parse("2222222222222222222222222222222222222222222222222222222222222222"), 1);
        var psbt = CreatePsbtWithInputs(new OutPoint(uint256.Parse("3333333333333333333333333333333333333333333333333333333333333333"), 0));
        var receivedCoins = new[] { CreateReceivedCoin(missingOutPoint, Money.Satoshis(10_000), CreateScript(2)) };

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => PayjoinReceiverPoller.EnsureContributedInputsPresent(psbt, receivedCoins));

        // Assert
        Assert.Contains(missingOutPoint.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureContributedInputsPresentThrowsWhenMultipleInputsMissing()
    {
        // Arrange
        var missingOutPoint1 = new OutPoint(uint256.Parse("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), 0);
        var missingOutPoint2 = new OutPoint(uint256.Parse("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), 1);
        var psbt = CreatePsbtWithInputs(new OutPoint(uint256.Parse("cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"), 2));
        var receivedCoins = new[]
        {
            CreateReceivedCoin(missingOutPoint1, Money.Satoshis(10_000), CreateScript(2)),
            CreateReceivedCoin(missingOutPoint2, Money.Satoshis(20_000), CreateScript(3))
        };

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => PayjoinReceiverPoller.EnsureContributedInputsPresent(psbt, receivedCoins));

        // Assert
        Assert.Contains(missingOutPoint1.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Contains(missingOutPoint2.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ClearSenderInputFinalizationClearsOnlySenderInputs()
    {
        // Arrange
        var receiverOutPoint = new OutPoint(uint256.Parse("4444444444444444444444444444444444444444444444444444444444444444"), 0);
        var senderOutPoint = new OutPoint(uint256.Parse("5555555555555555555555555555555555555555555555555555555555555555"), 1);
        var psbt = CreatePsbtWithInputs(receiverOutPoint, senderOutPoint);
        psbt.Inputs[0].FinalScriptSig = Script.Empty;
        psbt.Inputs[0].FinalScriptWitness = WitScript.Empty;
        psbt.Inputs[1].FinalScriptSig = Script.Empty;
        psbt.Inputs[1].FinalScriptWitness = WitScript.Empty;
        var receivedCoins = new[] { CreateReceivedCoin(receiverOutPoint, Money.Satoshis(20_000), CreateScript(3)) };

        // Act
        PayjoinReceiverPoller.ClearSenderInputFinalization(psbt, receivedCoins);

        // Assert
        Assert.NotNull(psbt.Inputs[0].FinalScriptSig);
        Assert.NotNull(psbt.Inputs[0].FinalScriptWitness);
        Assert.Null(psbt.Inputs[1].FinalScriptSig);
        Assert.Null(psbt.Inputs[1].FinalScriptWitness);
    }

    [Fact]
    public void ClearPartialSignaturesRemovesAllPartialSigs()
    {
        // Arrange
        var psbt = CreatePsbtWithInputs(
            new OutPoint(uint256.Parse("6666666666666666666666666666666666666666666666666666666666666666"), 0),
            new OutPoint(uint256.Parse("7777777777777777777777777777777777777777777777777777777777777777"), 1));
        using var key1 = new Key();
        using var key2 = new Key();
        psbt.Inputs[0].PartialSigs.Add(key1.PubKey, new TransactionSignature(key1.Sign(uint256.One), SigHash.All));
        psbt.Inputs[1].PartialSigs.Add(key2.PubKey, new TransactionSignature(key2.Sign(uint256.One), SigHash.All));

        // Act
        PayjoinReceiverPoller.ClearPartialSignatures(psbt);

        // Assert
        Assert.Empty(psbt.Inputs[0].PartialSigs);
        Assert.Empty(psbt.Inputs[1].PartialSigs);
    }

    [Fact]
    public async Task TryGetPersistedContributedCoinsAsyncReturnsNullWhenOutPointMissing()
    {
        // Arrange
        using var context = new TestContext();
        using var poller = CreatePoller(context.CreateStore());
        var session = CreateSession();

        // Act
        var result = await poller.TryGetPersistedContributedCoinsAsync(session, CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task TryGetPersistedContributedCoinsAsyncReturnsNullWhenContributedInputTransactionIdIsInvalid()
    {
        // Arrange
        using var context = new TestContext();
        using var poller = CreatePoller(context.CreateStore());
        var session = CreateSession(
            contributedInputTransactionId: "not-a-transaction-id",
            contributedInputOutputIndex: 0);

        // Act
        var result = await poller.TryGetPersistedContributedCoinsAsync(session, CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task TryGetPersistedContributedCoinsAsyncReturnsNullWhenContributedInputOutputIndexIsNegative()
    {
        // Arrange
        using var context = new TestContext();
        using var poller = CreatePoller(context.CreateStore());
        var session = CreateSession(
            contributedInputTransactionId: uint256.One.ToString(),
            contributedInputOutputIndex: -1);

        // Act
        var result = await poller.TryGetPersistedContributedCoinsAsync(session, CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task TryGetPersistedContributedCoinsAsyncReturnsNullWhenContributedInputOutputIndexOverflowsUInt()
    {
        // Arrange
        using var context = new TestContext();
        using var poller = CreatePoller(context.CreateStore());
        var session = CreateSession(
            contributedInputTransactionId: uint256.One.ToString(),
            contributedInputOutputIndex: (long)uint.MaxValue + 1);

        // Act
        var result = await poller.TryGetPersistedContributedCoinsAsync(session, CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task TryGetPersistedContributedCoinsAsyncReturnsNullWhenNetworkUnavailable()
    {
        // Arrange
        using var context = new TestContext();
        var networkProvider = CreateEmptyNetworkProvider();
        using var poller = CreatePoller(context.CreateStore(), networkProvider: networkProvider);
        var outPoint = new OutPoint(uint256.Parse("8888888888888888888888888888888888888888888888888888888888888888"), 2);
        var session = CreateSession(
            contributedInputTransactionId: outPoint.Hash.ToString(),
            contributedInputOutputIndex: outPoint.N);

        // Act
        var result = await poller.TryGetPersistedContributedCoinsAsync(session, CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ReceiverScriptOwnedCallbackMatchesExpectedScript()
    {
        // Arrange
        var callback = new PayjoinReceiverPoller.ReceiverScriptOwnedCallback(new byte[] { 0x01, 0x02, 0x03 });

        // Act
        var matching = callback.Callback(new byte[] { 0x01, 0x02, 0x03 });
        var nonMatching = callback.Callback(new byte[] { 0x01, 0x02 });

        // Assert
        Assert.True(matching);
        Assert.False(nonMatching);
    }

    [Fact]
    public void NoInputsSeenCallbackAlwaysReturnsFalse()
    {
        // Arrange
        var callback = new PayjoinReceiverPoller.NoInputsSeenCallback();

        // Act
        var first = callback.Callback(new PlainOutPoint("1111111111111111111111111111111111111111111111111111111111111111", 0));
        var second = callback.Callback(new PlainOutPoint("2222222222222222222222222222222222222222222222222222222222222222", 1));

        // Assert
        Assert.False(first);
        Assert.False(second);
    }

    [Fact]
    public void CloseRequestedBroadcastGuardReflectsCloseRequestedState()
    {
        // Arrange
        var openGuard = new PayjoinReceiverPoller.CloseRequestedBroadcastGuard(CreateSession(isCloseRequested: false));
        var closedGuard = new PayjoinReceiverPoller.CloseRequestedBroadcastGuard(CreateSession(isCloseRequested: true));

        // Act
        var open = openGuard.Callback(Array.Empty<byte>());
        var closed = closedGuard.Callback(Array.Empty<byte>());

        // Assert
        Assert.True(open);
        Assert.False(closed);
    }

    [Fact]
    public void SigningProcessPsbtReturnsStoredPsbt()
    {
        // Arrange
        var processor = new PayjoinReceiverPoller.SigningProcessPsbt("stored-psbt");

        // Act
        var result = processor.Callback("ignored");

        // Assert
        Assert.Equal("stored-psbt", result);
    }

    private static PayjoinReceiverPoller CreatePoller(
        PayjoinReceiverSessionStore sessionStore,
        BTCPayNetworkProvider? networkProvider = null)
    {
        var handlers = new PaymentMethodHandlerDictionary(Array.Empty<IPaymentMethodHandler>());
        return new PayjoinReceiverPoller(
            sessionStore,
            Substitute.For<IHttpClientFactory>(),
            networkProvider ?? CreateEmptyNetworkProvider(),
            null!,
            null!,
            handlers,
            null!,
            null!,
            Substitute.For<IPayjoinStoreSettingsRepository>(),
            Substitute.For<ILogger<PayjoinReceiverPoller>>());
    }

    private static ReceiveSession.Initialized CreateInitializedState()
    {
        return new ReceiveSessionState.Initialized(null!);
    }

    private static ReceiveSession.HasReplyableError CreateHasReplyableErrorState()
    {
        return new ReceiveSessionState.HasReplyableError(null!);
    }

    private static ReceiveSession.UncheckedOriginalPayload CreateUncheckedOriginalPayloadState()
    {
        return new ReceiveSessionState.UncheckedOriginalPayload(null!);
    }

    private static ReceiveSession.Monitor CreateMonitorState()
    {
        return new ReceiveSessionState.Monitor(null!);
    }

    private static PSBT CreatePsbtWithInputs(params OutPoint[] outPoints)
    {
        var network = Network.RegTest;
        var transaction = network.CreateTransaction();
        foreach (var outPoint in outPoints)
        {
            transaction.Inputs.Add(new TxIn(outPoint));
        }

        transaction.Outputs.Add(Money.Satoshis(1000), CreateScript(9));
        return PSBT.FromTransaction(transaction, network);
    }

    private static ReceivedCoin CreateReceivedCoin(OutPoint outPoint, Money amount, Script scriptPubKey)
    {
        return new ReceivedCoin
        {
            OutPoint = outPoint,
            ScriptPubKey = scriptPubKey,
            Value = amount,
            Coin = new Coin(outPoint, new TxOut(amount, scriptPubKey)),
            Timestamp = DateTimeOffset.UtcNow,
            Confirmations = 1
        };
    }

    private static Script CreateScript(int seed)
    {
        var keyBytes = Enumerable.Repeat((byte)seed, 32).ToArray();
        using var key = new Key(keyBytes);
        return key.PubKey.WitHash.ScriptPubKey;
    }

    private static PayjoinReceiverSessionState CreateSession(
        string? invoiceId = null,
        string? storeId = null,
        string? receiverAddress = null,
        SystemUri? ohttpRelayUrl = null,
        DateTimeOffset? monitoringExpiresAt = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? updatedAt = null,
        bool isCloseRequested = false,
        InvoiceStatus? closeInvoiceStatus = null,
        DateTimeOffset? closeRequestedAt = null,
        bool initializedPollAfterCloseRequestConsumed = false,
        string? contributedInputTransactionId = null,
        long? contributedInputOutputIndex = null,
        IEnumerable<string>? events = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new PayjoinReceiverSessionState(
            invoiceId ?? "invoice-1",
            storeId ?? "store-1",
            receiverAddress ?? "bcrt1qexampleaddress0000000000000000000000000",
            ohttpRelayUrl ?? new SystemUri("https://relay.example/"),
            monitoringExpiresAt ?? now.AddHours(1),
            createdAt ?? now,
            updatedAt ?? now,
            isCloseRequested,
            closeInvoiceStatus,
            closeRequestedAt,
            initializedPollAfterCloseRequestConsumed,
            contributedInputTransactionId,
            contributedInputOutputIndex,
            events);
    }

    private static BTCPayNetworkProvider CreateEmptyNetworkProvider()
    {
        return new BTCPayNetworkProvider(
            Array.Empty<BTCPayNetworkBase>(),
            Substitute.For<NBXplorerNetworkProvider>(ChainName.Regtest),
            Substitute.For<BTCPayServer.Logging.Logs>());
    }

    private sealed class TestContext : IDisposable
    {
        private readonly TestPayjoinPluginDbContextFactory _dbContextFactory = new();

        public PayjoinReceiverSessionStore CreateStore() => new(_dbContextFactory);

        public void Dispose()
        {
            using var db = _dbContextFactory.CreateContext();
            db.Database.EnsureDeleted();
        }
    }

    private sealed class TestPayjoinPluginDbContextFactory : PayjoinPluginDbContextFactory
    {
        private readonly DbContextOptions<PayjoinPluginDbContext> _dbContextOptions;

        public TestPayjoinPluginDbContextFactory()
            : base(Options.Create(new DatabaseOptions
            {
                ConnectionString = "Host=localhost;Database=payjoin-plugin-tests;Username=postgres"
            }))
        {
            var databaseRoot = new InMemoryDatabaseRoot();
            var databaseName = $"payjoin-poller-tests-{Guid.NewGuid():N}";
            _dbContextOptions = new DbContextOptionsBuilder<PayjoinPluginDbContext>()
                .UseInMemoryDatabase(databaseName, databaseRoot)
                .Options;

            using var db = CreateContext();
            db.Database.EnsureCreated();
        }

        public override PayjoinPluginDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptionsAction = null)
        {
            return new PayjoinPluginDbContext(_dbContextOptions);
        }
    }
}
