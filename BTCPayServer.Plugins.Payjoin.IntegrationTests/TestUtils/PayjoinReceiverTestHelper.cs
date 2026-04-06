using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Tests;
using System.Globalization;
using System.Text;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;

internal static class PayjoinReceiverTestHelper
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ReceiverSessionCreationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReceiverSessionRemovalTimeout = TimeSpan.FromSeconds(30);

    public static Task AssertReceiverSessionEventuallyCreatedAsync(ServerTester tester, string invoiceId, CancellationToken cancellationToken)
    {
        return AssertReceiverSessionStateAsync(tester, invoiceId, shouldExist: true, cancellationToken);
    }

    public static Task AssertReceiverSessionEventuallyRemovedAsync(ServerTester tester, string invoiceId, CancellationToken cancellationToken)
    {
        return AssertReceiverSessionStateAsync(tester, invoiceId, shouldExist: false, cancellationToken);
    }

    public static async Task AssertReceiverSessionEventuallyCloseRequestedOrRemovedAsync(ServerTester tester, string invoiceId, CancellationToken cancellationToken)
    {
        var sessionStore = tester.PayTester.GetService<PayjoinReceiverSessionStore>();
        var maxAttempts = GetAttemptCount(ReceiverSessionRemovalTimeout);

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!sessionStore.TryGetSession(invoiceId, out var session) || session?.IsCloseRequested == true)
            {
                return;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(true);
        }

        Assert.Fail($"Expected receiver session for invoice '{invoiceId}' to be marked for closure or removed.");
    }

    public static PayjoinReceiverSessionState GetRequiredReceiverSession(ServerTester tester, string invoiceId)
    {
        var sessionStore = tester.PayTester.GetService<PayjoinReceiverSessionStore>();
        Assert.True(sessionStore.TryGetSession(invoiceId, out var session));
        Assert.NotNull(session);
        return session;
    }

    public static async Task<string> GetReceiverSideDiagnosticsAsync(ServerTester tester, string invoiceId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tester);
        ArgumentException.ThrowIfNullOrWhiteSpace(invoiceId);

        var invoiceRepository = tester.PayTester.GetService<InvoiceRepository>();
        var invoice = await invoiceRepository.GetInvoice(invoiceId).WaitAsync(cancellationToken).ConfigureAwait(true);
        var sessionStore = tester.PayTester.GetService<PayjoinReceiverSessionStore>();

        var diagnostics = new StringBuilder();
        diagnostics.Append("InvoiceExists=").Append(invoice is not null);
        diagnostics.Append(", InvoiceStatus=").Append(invoice?.GetInvoiceState().Status.ToString() ?? "<missing>");

        if (!sessionStore.TryGetSession(invoiceId, out var session) || session is null)
        {
            diagnostics.Append(", SessionExists=false");
            return diagnostics.ToString();
        }

        var events = session.GetEvents();
        diagnostics.Append(", SessionExists=true");
        diagnostics.Append(", SessionUpdatedAt=").Append(session.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        diagnostics.Append(", MonitoringExpiresAt=").Append(session.MonitoringExpiresAt.ToString("O", CultureInfo.InvariantCulture));
        diagnostics.Append(", IsCloseRequested=").Append(session.IsCloseRequested);
        diagnostics.Append(", CloseInvoiceStatus=").Append(session.CloseInvoiceStatus?.ToString() ?? "<null>");
        diagnostics.Append(", EventCount=").Append(events.Length);

        if (events.Length > 0)
        {
            diagnostics.Append(", RecentEvents=[");
            diagnostics.Append(string.Join(" | ", events.TakeLast(3).Select(FormatReceiverEventForDiagnostics)));
            diagnostics.Append(']');
        }

        return diagnostics.ToString();
    }

    internal static async Task<string> TryGetReceiverSideDiagnosticsAsync(ServerTester tester, string invoiceId)
    {
        try
        {
            return await GetReceiverSideDiagnosticsAsync(tester, invoiceId, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or ObjectDisposedException)
        {
            return $"<failed to collect receiver diagnostics: {ex.GetType().Name}: {ex.Message}>";
        }
    }

    private static async Task AssertReceiverSessionStateAsync(ServerTester tester, string invoiceId, bool shouldExist, CancellationToken cancellationToken)
    {
        var sessionStore = tester.PayTester.GetService<PayjoinReceiverSessionStore>();
        var timeout = shouldExist ? ReceiverSessionCreationTimeout : ReceiverSessionRemovalTimeout;
        var maxAttempts = GetAttemptCount(timeout);

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var exists = sessionStore.TryGetSession(invoiceId, out _);
            if (exists == shouldExist)
            {
                return;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(true);
        }

        Assert.Fail(shouldExist
            ? $"Expected receiver session for invoice '{invoiceId}' to be created."
            : $"Expected receiver session for invoice '{invoiceId}' to be removed.");
    }

    private static string FormatReceiverEventForDiagnostics(string @event)
    {
        const int maxLength = 160;
        var normalized = @event.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        return normalized.Length <= maxLength ? normalized : $"{normalized[..maxLength]}...";
    }

    private static int GetAttemptCount(TimeSpan timeout)
    {
        var attempts = (int)Math.Ceiling(timeout.TotalMilliseconds / PollInterval.TotalMilliseconds);
        return Math.Max(attempts, 1);
    }
}
