using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
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

public class PayjoinReceiverSessionGuardTests
{
    [Fact]
    public void TryExpireSessionRemovesExpiredSession()
    {
        using var context = new TestContext();
        var sessionStore = context.CreateStore();
        var guard = CreateGuard(sessionStore);
        var session = sessionStore.CreateSession(
            "invoice-expired",
            "bcrt1qexampleaddress0000000000000000000000000",
            "store-1",
            new SystemUri("https://relay.example/"),
            DateTimeOffset.UtcNow.AddMinutes(-1),
            out _);

        var expired = guard.TryExpireSession(session);

        Assert.True(expired);
        Assert.False(sessionStore.TryGetSession(session.InvoiceId, out _));
    }

    [Fact]
    public void TryExpireSessionKeepsActiveSession()
    {
        using var context = new TestContext();
        var sessionStore = context.CreateStore();
        var guard = CreateGuard(sessionStore);
        var session = sessionStore.CreateSession(
            "invoice-active",
            "bcrt1qexampleaddress0000000000000000000000000",
            "store-1",
            new SystemUri("https://relay.example/"),
            DateTimeOffset.UtcNow.AddMinutes(10),
            out _);

        var expired = guard.TryExpireSession(session);

        Assert.False(expired);
        Assert.True(sessionStore.TryGetSession(session.InvoiceId, out _));
    }

    [Fact]
    public void TryRemoveCloseRequestedSessionReturnsFalseWhenCloseNotRequested()
    {
        using var context = new TestContext();
        var sessionStore = context.CreateStore();
        var guard = CreateGuard(sessionStore);
        var session = sessionStore.CreateSession(
            "invoice-open",
            "bcrt1qexampleaddress0000000000000000000000000",
            "store-1",
            new SystemUri("https://relay.example/"),
            DateTimeOffset.UtcNow.AddMinutes(10),
            out _);
        using var state = CreateMonitorState();

        var removed = guard.TryRemoveCloseRequestedSession(session, state);

        Assert.False(removed);
        Assert.True(sessionStore.TryGetSession(session.InvoiceId, out _));
    }

    [Fact]
    public void TryRemoveCloseRequestedSessionKeepsSessionWhenStateHasReplyableError()
    {
        using var context = new TestContext();
        var sessionStore = context.CreateStore();
        var guard = CreateGuard(sessionStore);
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

        var removed = guard.TryRemoveCloseRequestedSession(closeRequested!, state);

        Assert.False(removed);
        Assert.True(sessionStore.TryGetSession(session.InvoiceId, out _));
    }

    [Fact]
    public void TryRemoveCloseRequestedSessionKeepsSessionWhenInitializedPollAfterCloseRequestNotConsumed()
    {
        using var context = new TestContext();
        var sessionStore = context.CreateStore();
        var guard = CreateGuard(sessionStore);
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

        var removed = guard.TryRemoveCloseRequestedSession(closeRequested!, state);

        Assert.False(removed);
        Assert.True(sessionStore.TryGetSession(session.InvoiceId, out _));
    }

    [Fact]
    public void TryRemoveCloseRequestedSessionRemovesSessionWhenInitializedPollAfterCloseRequestConsumed()
    {
        using var context = new TestContext();
        var sessionStore = context.CreateStore();
        var guard = CreateGuard(sessionStore);
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

        var removed = guard.TryRemoveCloseRequestedSession(closeRequested!, state);

        Assert.True(removed);
        Assert.False(sessionStore.TryGetSession(session.InvoiceId, out _));
    }

    [Fact]
    public void TryRemoveCloseRequestedSessionRemovesSessionWhenStateCannotReply()
    {
        using var context = new TestContext();
        var sessionStore = context.CreateStore();
        var guard = CreateGuard(sessionStore);
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

        var removed = guard.TryRemoveCloseRequestedSession(closeRequested!, state);

        Assert.True(removed);
        Assert.False(sessionStore.TryGetSession(session.InvoiceId, out _));
    }

    [Fact]
    public void TryRemoveCloseRequestedSessionKeepsSessionWhenStateCanReply()
    {
        using var context = new TestContext();
        var sessionStore = context.CreateStore();
        var guard = CreateGuard(sessionStore);
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

        var removed = guard.TryRemoveCloseRequestedSession(closeRequested!, state);

        Assert.False(removed);
        Assert.True(sessionStore.TryGetSession(session.InvoiceId, out _));
    }

    private static PayjoinReceiverSessionGuard CreateGuard(
        PayjoinReceiverSessionStore sessionStore,
        BTCPayNetworkProvider? networkProvider = null)
    {
        return new PayjoinReceiverSessionGuard(
            sessionStore,
            networkProvider ?? CreateEmptyNetworkProvider(),
            null!,
            NullLogger<PayjoinReceiverSessionGuard>.Instance);
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
        private readonly PostgresPayjoinUniqueConstraintViolationDetector _uniqueConstraintViolationDetector = new();

        public PayjoinReceiverSessionStore CreateStore() => new(_dbContextFactory, _uniqueConstraintViolationDetector);

        public void Dispose()
        {
            using var db = _dbContextFactory.CreateContext();
            db.Database.EnsureDeleted();
        }
    }

    private sealed class TestPayjoinPluginDbContextFactory : PayjoinPluginDbContextFactory
    {
        private static readonly InMemoryDatabaseRoot SharedDatabaseRoot = new();
        private readonly DbContextOptions<PayjoinPluginDbContext> _dbContextOptions;

        public TestPayjoinPluginDbContextFactory()
            : base(Options.Create(new DatabaseOptions
            {
                ConnectionString = "Host=localhost;Database=payjoin-plugin-tests;Username=postgres"
            }))
        {
            var databaseName = $"payjoin-session-guard-tests-{Guid.NewGuid():N}";
            _dbContextOptions = new DbContextOptionsBuilder<PayjoinPluginDbContext>()
                .UseInMemoryDatabase(databaseName, SharedDatabaseRoot)
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
