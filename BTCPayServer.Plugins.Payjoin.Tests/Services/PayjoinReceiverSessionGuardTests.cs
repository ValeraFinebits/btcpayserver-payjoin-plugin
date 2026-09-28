using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NBXplorer;
using NSubstitute;
using Payjoin;
using Xunit;
using ReceiveSessionState = global::Payjoin.ReceiveSession;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinReceiverSessionGuardTests
{
    [Fact]
    public void TryExpireSessionRemovesExpiredSession()
    {
        using var context = new SessionStoreFixture();
        var sessionStore = context.CreateStore();
        var guard = CreateGuard(sessionStore);
        var session = sessionStore.GetOrCreateSession(
            "invoice-expired",
            "bcrt1qexampleaddress0000000000000000000000000",
            "store-1",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            ["bootstrap-event"]);

        var expired = guard.TryExpireSession(session);

        Assert.True(expired);
        Assert.False(sessionStore.TryGetSession(session.InvoiceId, out _));
    }

    [Fact]
    public void TryExpireSessionKeepsActiveSession()
    {
        using var context = new SessionStoreFixture();
        var sessionStore = context.CreateStore();
        var guard = CreateGuard(sessionStore);
        var session = sessionStore.GetOrCreateSession(
            "invoice-active",
            "bcrt1qexampleaddress0000000000000000000000000",
            "store-1",
            DateTimeOffset.UtcNow.AddMinutes(10),
            ["bootstrap-event"]);

        var expired = guard.TryExpireSession(session);

        Assert.False(expired);
        Assert.True(sessionStore.TryGetSession(session.InvoiceId, out _));
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(10, false)]
    public void GuardExpiresASessionExactlyWhenItStopsBeingServable(int monitoringMinutesFromNow, bool expectedExpired)
    {
        using var context = new SessionStoreFixture();
        var sessionStore = context.CreateStore();
        var guard = CreateGuard(sessionStore);
        var session = sessionStore.GetOrCreateSession(
            $"invoice-deadline-{monitoringMinutesFromNow}",
            "bcrt1qexampleaddress0000000000000000000000000",
            "store-1",
            DateTimeOffset.UtcNow.AddMinutes(monitoringMinutesFromNow),
            ["bootstrap-event"]);

        Assert.Equal(!expectedExpired, session.GetServability().IsServable());
        Assert.Equal(expectedExpired, guard.TryExpireSession(session));
    }

    [Fact]
    public void TryRemoveCloseRequestedSessionReturnsFalseWhenCloseNotRequested()
    {
        using var context = new SessionStoreFixture();
        var sessionStore = context.CreateStore();
        var guard = CreateGuard(sessionStore);
        var session = sessionStore.GetOrCreateSession(
            "invoice-open",
            "bcrt1qexampleaddress0000000000000000000000000",
            "store-1",
            DateTimeOffset.UtcNow.AddMinutes(10),
            ["bootstrap-event"]);
        using var state = CreateMonitorState();

        var removed = guard.TryRemoveCloseRequestedSession(session, state);

        Assert.False(removed);
        Assert.True(sessionStore.TryGetSession(session.InvoiceId, out _));
    }

    [Fact]
    public void TryRemoveCloseRequestedSessionKeepsSessionWhenStateHasReplyableError()
    {
        using var context = new SessionStoreFixture();
        var sessionStore = context.CreateStore();
        var guard = CreateGuard(sessionStore);
        var session = sessionStore.GetOrCreateSession(
            "invoice-close-replyable-error",
            "bcrt1qexampleaddress0000000000000000000000000",
            "store-1",
            DateTimeOffset.UtcNow.AddMinutes(10),
            ["bootstrap-event"]);
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
        using var context = new SessionStoreFixture();
        var sessionStore = context.CreateStore();
        var guard = CreateGuard(sessionStore);
        var session = sessionStore.GetOrCreateSession(
            "invoice-close-initialized-keep",
            "bcrt1qexampleaddress0000000000000000000000000",
            "store-1",
            DateTimeOffset.UtcNow.AddMinutes(10),
            ["bootstrap-event"]);
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
        using var context = new SessionStoreFixture();
        var sessionStore = context.CreateStore();
        var guard = CreateGuard(sessionStore);
        var closeRequested = CreateCloseRequestedSession(
            context,
            sessionStore,
            "invoice-close-initialized-remove",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            initializedPollAfterCloseRequestConsumed: true);
        using var state = CreateInitializedState();

        var removed = guard.TryRemoveCloseRequestedSession(closeRequested!, state);

        Assert.True(removed);
        Assert.False(sessionStore.TryGetSession(closeRequested.InvoiceId, out _));
    }

    [Fact]
    public void TryRemoveCloseRequestedSessionKeepsSessionBrieflyWhenInitializedPollAfterCloseRequestConsumedForSettledInvoice()
    {
        using var context = new SessionStoreFixture();
        var sessionStore = context.CreateStore();
        var guard = CreateGuard(sessionStore);
        var closeRequested = CreateCloseRequestedSession(
            context,
            sessionStore,
            "invoice-close-initialized-settled-keep",
            DateTimeOffset.UtcNow,
            initializedPollAfterCloseRequestConsumed: true,
            closeInvoiceStatus: InvoiceStatus.Settled);
        using var state = CreateInitializedState();

        var removed = guard.TryRemoveCloseRequestedSession(closeRequested!, state);

        Assert.False(removed);
        Assert.True(sessionStore.TryGetSession(closeRequested.InvoiceId, out _));
    }

    [Fact]
    public void TryRemoveCloseRequestedSessionRemovesSessionWhenInitializedPollAfterCloseRequestConsumedForSettledInvoiceAfterGrace()
    {
        using var context = new SessionStoreFixture();
        var sessionStore = context.CreateStore();
        var guard = CreateGuard(sessionStore);
        var closeRequested = CreateCloseRequestedSession(
            context,
            sessionStore,
            "invoice-close-initialized-settled-remove",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            initializedPollAfterCloseRequestConsumed: true,
            closeInvoiceStatus: InvoiceStatus.Settled);
        using var state = CreateInitializedState();

        var removed = guard.TryRemoveCloseRequestedSession(closeRequested!, state);

        Assert.True(removed);
        Assert.False(sessionStore.TryGetSession(closeRequested.InvoiceId, out _));
    }

    [Fact]
    public void TryRemoveCloseRequestedSessionKeepsSessionBrieflyWhenInitializedPollAfterCloseRequestConsumedRecently()
    {
        using var context = new SessionStoreFixture();
        var sessionStore = context.CreateStore();
        var guard = CreateGuard(sessionStore);
        var closeRequested = CreateCloseRequestedSession(
            context,
            sessionStore,
            "invoice-close-initialized-grace",
            DateTimeOffset.UtcNow,
            initializedPollAfterCloseRequestConsumed: true);
        using var state = CreateInitializedState();

        var removed = guard.TryRemoveCloseRequestedSession(closeRequested!, state);

        Assert.False(removed);
        Assert.True(sessionStore.TryGetSession(closeRequested.InvoiceId, out _));
    }

    [Fact]
    public void TryRemoveCloseRequestedSessionRemovesSessionWhenStateCannotReply()
    {
        using var context = new SessionStoreFixture();
        var sessionStore = context.CreateStore();
        var guard = CreateGuard(sessionStore);
        var session = sessionStore.GetOrCreateSession(
            "invoice-close-remove",
            "bcrt1qexampleaddress0000000000000000000000000",
            "store-1",
            DateTimeOffset.UtcNow.AddMinutes(10),
            ["bootstrap-event"]);
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
        using var context = new SessionStoreFixture();
        var sessionStore = context.CreateStore();
        var guard = CreateGuard(sessionStore);
        var session = sessionStore.GetOrCreateSession(
            "invoice-close-keep",
            "bcrt1qexampleaddress0000000000000000000000000",
            "store-1",
            DateTimeOffset.UtcNow.AddMinutes(10),
            ["bootstrap-event"]);
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

    private static PayjoinReceiverSessionState CreateCloseRequestedSession(
        SessionStoreFixture context,
        PayjoinReceiverSessionStore sessionStore,
        string invoiceId,
        DateTimeOffset closeRequestedAt,
        bool initializedPollAfterCloseRequestConsumed,
        InvoiceStatus closeInvoiceStatus = InvoiceStatus.Expired)
    {
        var session = sessionStore.GetOrCreateSession(
            invoiceId,
            "bcrt1qexampleaddress0000000000000000000000000",
            "store-1",
            DateTimeOffset.UtcNow.AddMinutes(10),
            ["bootstrap-event"]);

        Assert.True(sessionStore.RequestClose(session.InvoiceId, closeInvoiceStatus));

        using var db = context.CreateDbContext();
        var sessionData = db.ReceiverSessions.Single(x => x.InvoiceId == invoiceId);
        sessionData.CloseRequestedAt = closeRequestedAt;
        sessionData.InitializedPollAfterCloseRequestConsumed = initializedPollAfterCloseRequestConsumed;
        db.SaveChanges();

        Assert.True(sessionStore.TryGetSession(session.InvoiceId, out var closeRequested));
        return closeRequested!;
    }

}
