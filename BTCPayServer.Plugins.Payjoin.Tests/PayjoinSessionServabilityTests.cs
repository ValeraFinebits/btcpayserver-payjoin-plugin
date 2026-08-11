using BTCPayServer.Plugins.Payjoin.Services;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class PayjoinSessionServabilityTests
{
    private const string Destination = TestSessionStates.DefaultReceiverAddress;

    private static PayjoinReceiverSessionState CreateSession(
        bool isCloseRequested = false,
        TimeSpan? monitoringRemaining = null,
        string[]? events = null,
        string receiverAddress = Destination) =>
        TestSessionStates.Create(
            receiverAddress: receiverAddress,
            monitoringRemaining: monitoringRemaining,
            isCloseRequested: isCloseRequested,
            events: events);

    [Fact]
    public void AddressComparisonIsCaseSensitive()
    {
        var session = CreateSession(receiverAddress: Destination.ToUpperInvariant());

        Assert.False(session.GetServability().MatchesInvoice(Destination));
    }

    [Fact]
    public void SessionMatchingTheInvoiceIsReused()
    {
        Assert.Equal(
            PayjoinPersistedSessionDecision.Reuse,
            CreateSession().GetServability().Decide(Destination));
    }

    [Fact]
    public void EmptyEventLogIsReportedAsRebuildRatherThanNotServable()
    {
        var session = CreateSession(events: [], monitoringRemaining: TimeSpan.FromMinutes(-1));

        Assert.Equal(
            PayjoinPersistedSessionDecision.RebuildEmptyEventLog,
            session.GetServability().Decide(Destination));
    }

    [Theory]
    [InlineData(true, 60)]
    [InlineData(false, -1)]
    public void ClosedOrExpiredSessionIsLeftToThePoller(bool isCloseRequested, int monitoringMinutes)
    {
        var session = CreateSession(
            isCloseRequested: isCloseRequested,
            monitoringRemaining: TimeSpan.FromMinutes(monitoringMinutes));

        Assert.Equal(
            PayjoinPersistedSessionDecision.NotServable,
            session.GetServability().Decide(Destination));
    }

    [Fact]
    public void SessionForAnotherAddressIsRebuilt()
    {
        Assert.Equal(
            PayjoinPersistedSessionDecision.RebuildAddressMismatch,
            CreateSession(receiverAddress: "bcrt1qsomewhereelse").GetServability().Decide(Destination));
    }

    [Fact]
    public void SessionWithoutEventsIsNotServable()
    {
        Assert.False(CreateSession(events: []).GetServability().IsServable());
    }
}
