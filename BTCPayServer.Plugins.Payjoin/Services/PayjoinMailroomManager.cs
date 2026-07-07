using BTCPayServer.Plugins.Payjoin.Models;
using Microsoft.Extensions.Logging;
using Payjoin;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SystemUri = System.Uri;

namespace BTCPayServer.Plugins.Payjoin.Services;

/// <summary>
/// Coordinates directory and relay selection for payjoin bootstrap and request-time relay transport.
/// </summary>
internal sealed class PayjoinMailroomManager
{
    internal static readonly TimeSpan DefaultFailedRelayCacheDuration = TimeSpan.FromMinutes(10);

    private static readonly Action<ILogger, string, string, string, Exception?> LogRelayFetchAttempt =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Debug,
            new EventId(1, nameof(SelectBootstrapRouteAsync)),
            "Fetching OHTTP keys for invoice {InvoiceId} from relay {OhttpRelayUrl} and directory {DirectoryUrl}.");

    private static readonly Action<ILogger, string, string, string, string, Exception?> LogRelayFetchRetryableFailure =
        LoggerMessage.Define<string, string, string, string>(
            LogLevel.Warning,
            new EventId(2, nameof(SelectBootstrapRouteAsync)),
            "Retryable OHTTP keys fetch failure for invoice {InvoiceId} from relay {OhttpRelayUrl} and directory {DirectoryUrl}: {Message}");

    private static readonly Action<ILogger, string, string, string, string, Exception?> LogRelayFetchNonRetryableFailure =
        LoggerMessage.Define<string, string, string, string>(
            LogLevel.Warning,
            new EventId(3, nameof(SelectBootstrapRouteAsync)),
            "Non-retryable OHTTP keys fetch failure for invoice {InvoiceId} from relay {OhttpRelayUrl} and directory {DirectoryUrl}: {Message}");

    private static readonly Action<ILogger, string, string, string, Exception?> LogRelaySelected =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Information,
            new EventId(4, nameof(SelectBootstrapRouteAsync)),
            "Selected OHTTP relay {OhttpRelayUrl} and directory {DirectoryUrl} for payjoin receiver session on invoice {InvoiceId}.");

    private static readonly Action<ILogger, string, int, int, Exception?> LogAllRoutesFailed =
        LoggerMessage.Define<string, int, int>(
            LogLevel.Warning,
            new EventId(5, nameof(SelectBootstrapRouteAsync)),
            "Unable to fetch OHTTP keys for invoice {InvoiceId}; all configured routes failed or are temporarily unavailable across {DirectoryCount} directories and {RelayCount} relays.");

    private static readonly Action<ILogger, string, int, int, Exception?> LogRelaySelectionMissingConfiguration =
        LoggerMessage.Define<string, int, int>(
            LogLevel.Warning,
            new EventId(6, nameof(SelectBootstrapRouteAsync)),
            "Unable to select an OHTTP relay for invoice {InvoiceId}; configuration contains {DirectoryCount} directories and {RelayCount} relays.");

    private readonly ILogger<PayjoinMailroomManager> _logger;
    private readonly TimeSpan _failedRelayCacheDuration;
    private readonly Func<SystemUri, string, string, CancellationToken, Task<PayjoinOhttpKeysFetchResult>> _fetchKeysAsync;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _temporarilyUnavailableRoutes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _temporarilyUnavailableRelays = new(StringComparer.OrdinalIgnoreCase);

    public PayjoinMailroomManager(
        PayjoinOhttpKeysProvider ohttpKeysProvider,
        ILogger<PayjoinMailroomManager> logger)
        : this(ohttpKeysProvider, logger, DefaultFailedRelayCacheDuration)
    {
    }

    internal PayjoinMailroomManager(
        PayjoinOhttpKeysProvider ohttpKeysProvider,
        ILogger<PayjoinMailroomManager> logger,
        TimeSpan failedRelayCacheDuration)
        : this(logger, failedRelayCacheDuration, (ohttpKeysProvider ?? throw new ArgumentNullException(nameof(ohttpKeysProvider))).FetchKeysAsync)
    {
    }

    internal PayjoinMailroomManager(
        PayjoinOhttpKeysProvider ohttpKeysProvider,
        ILogger<PayjoinMailroomManager> logger,
        TimeSpan failedRelayCacheDuration,
        Func<SystemUri, string, string, CancellationToken, Task<PayjoinOhttpKeysFetchResult>> fetchKeysAsync)
        : this(logger, failedRelayCacheDuration, fetchKeysAsync)
    {
        ArgumentNullException.ThrowIfNull(ohttpKeysProvider);
    }

    internal PayjoinMailroomManager(
        ILogger<PayjoinMailroomManager> logger,
        TimeSpan failedRelayCacheDuration,
        Func<SystemUri, string, string, CancellationToken, Task<PayjoinOhttpKeysFetchResult>> fetchKeysAsync)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(fetchKeysAsync);
        _logger = logger;
        _failedRelayCacheDuration = failedRelayCacheDuration;
        _fetchKeysAsync = fetchKeysAsync;
    }

    internal async Task<SelectedPayjoinBootstrapRoute?> SelectBootstrapRouteAsync(
        PayjoinStoreSettings storeSettings,
        string storeId,
        string invoiceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(storeSettings);
        var directoryUrls = storeSettings.GetEffectiveDirectoryUrls();
        var relayUrls = storeSettings.GetEffectiveOhttpRelayUrls();
        if (directoryUrls.Count == 0 || relayUrls.Count == 0)
        {
            LogRelaySelectionMissingConfiguration(_logger, invoiceId, directoryUrls.Count, relayUrls.Count, null);
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var orderedDirectories = OrderDirectoryUrls(directoryUrls);
        var orderedRelays = OrderRelayUrls(relayUrls);
        var selected = await TrySelectRouteAsync(orderedDirectories, orderedRelays, storeId, invoiceId, now, cancellationToken).ConfigureAwait(false);
        if (selected.Route is not null)
        {
            return selected.Route;
        }

        LogAllRoutesFailed(_logger, invoiceId, directoryUrls.Count, relayUrls.Count, null);
        return null;
    }

    internal static IReadOnlyList<SystemUri> OrderDirectoryUrls(IReadOnlyList<SystemUri> directoryUrls)
    {
        return OrderUrls(directoryUrls);
    }

    internal static IReadOnlyList<SystemUri> OrderRelayUrls(IReadOnlyList<SystemUri> relayUrls)
    {
        return OrderUrls(relayUrls);
    }

    internal SystemUri? ChooseRelayForRequest(PayjoinStoreSettings storeSettings)
    {
        ArgumentNullException.ThrowIfNull(storeSettings);

        return ChooseRelayForRequest(storeSettings.GetEffectiveOhttpRelayUrls());
    }

    internal SystemUri? ChooseRelayForRequest(IReadOnlyList<SystemUri> relayUrls)
    {
        ArgumentNullException.ThrowIfNull(relayUrls);

        relayUrls = OrderRelayUrls(relayUrls);
        if (relayUrls.Count == 0)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var relayUrl in relayUrls)
        {
            if (!IsRelayTemporarilyUnavailable(relayUrl, now))
            {
                return relayUrl;
            }
        }

        return null;
    }

    internal void MarkRelayTemporarilyUnavailable(SystemUri relayUrl)
    {
        ArgumentNullException.ThrowIfNull(relayUrl);
        _temporarilyUnavailableRelays[CreateRelayKey(relayUrl)] = DateTimeOffset.UtcNow;
    }

    private static IReadOnlyList<SystemUri> OrderUrls(IReadOnlyList<SystemUri> urls)
    {
        ArgumentNullException.ThrowIfNull(urls);
        var ordered = urls.ToArray();
        for (var i = ordered.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (ordered[i], ordered[j]) = (ordered[j], ordered[i]);
        }

        return ordered;
    }

    private async Task<RouteSelectionAttempt> TrySelectRouteAsync(
        IReadOnlyList<SystemUri> orderedDirectories,
        IReadOnlyList<SystemUri> orderedRelays,
        string storeId,
        string invoiceId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var directoryUrl in orderedDirectories)
        {
            var selected = await TrySelectRelayForDirectoryAsync(directoryUrl, orderedRelays, storeId, invoiceId, now, cancellationToken).ConfigureAwait(false);
            if (selected.Route is not null)
            {
                return selected;
            }
        }

        return RouteSelectionAttempt.ContinueNextDirectory();
    }

    private async Task<RouteSelectionAttempt> TrySelectRelayForDirectoryAsync(
        SystemUri directoryUrl,
        IReadOnlyList<SystemUri> orderedRelays,
        string storeId,
        string invoiceId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var remainingRelays = orderedRelays.ToList();
        while (remainingRelays.Count > 0)
        {
            var relayUrl = ChooseRelayForBootstrap(directoryUrl, remainingRelays, now);
            if (relayUrl is null)
            {
                break;
            }

            var selected = await TryFetchKeysForRouteAsync(directoryUrl, relayUrl, storeId, invoiceId, cancellationToken).ConfigureAwait(false);
            if (selected.Disposition == RouteSelectionDisposition.ContinueCurrentDirectory)
            {
                if (selected.Route is not null)
                {
                    return selected;
                }

                RemoveRelay(remainingRelays, relayUrl);
                continue;
            }

            return selected;
        }

        return RouteSelectionAttempt.ContinueNextDirectory();
    }

    private async Task<RouteSelectionAttempt> TryFetchKeysForRouteAsync(
        SystemUri directoryUrl,
        SystemUri relayUrl,
        string storeId,
        string invoiceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LogRelayFetchAttempt(_logger, invoiceId, relayUrl.AbsoluteUri, directoryUrl.AbsoluteUri, null);
        var result = await _fetchKeysAsync(relayUrl, directoryUrl.AbsoluteUri, storeId, cancellationToken).ConfigureAwait(false);

        switch (result.Status)
        {
            case PayjoinOhttpKeysFetchStatus.Success:
                if (result.OhttpKeys is null)
                {
                    MarkRouteUnavailable(directoryUrl, relayUrl);
                    return RouteSelectionAttempt.ContinueCurrentDirectory();
                }

                _temporarilyUnavailableRoutes.TryRemove(CreateRouteKey(directoryUrl, relayUrl), out _);
                LogRelaySelected(_logger, relayUrl.AbsoluteUri, directoryUrl.AbsoluteUri, invoiceId, null);
                return RouteSelectionAttempt.Selected(new SelectedPayjoinBootstrapRoute(directoryUrl, relayUrl, result.OhttpKeys));
            case PayjoinOhttpKeysFetchStatus.RetryableFailure:
                MarkRouteUnavailable(directoryUrl, relayUrl);
                LogRelayFetchRetryableFailure(_logger, invoiceId, relayUrl.AbsoluteUri, directoryUrl.AbsoluteUri, result.Exception?.Message ?? string.Empty, result.Exception);
                return RouteSelectionAttempt.ContinueCurrentDirectory();
            case PayjoinOhttpKeysFetchStatus.NonRetryableFailure:
                LogRelayFetchNonRetryableFailure(_logger, invoiceId, relayUrl.AbsoluteUri, directoryUrl.AbsoluteUri, result.Exception?.Message ?? string.Empty, result.Exception);
                return RouteSelectionAttempt.ContinueNextDirectory();
            default:
                MarkRouteUnavailable(directoryUrl, relayUrl);
                return RouteSelectionAttempt.ContinueCurrentDirectory();
        }
    }

    private bool IsRouteTemporarilyUnavailable(SystemUri directoryUrl, SystemUri relayUrl, DateTimeOffset now)
    {
        var key = CreateRouteKey(directoryUrl, relayUrl);
        if (!_temporarilyUnavailableRoutes.TryGetValue(key, out var failedAt))
        {
            return false;
        }

        if (now - failedAt < _failedRelayCacheDuration)
        {
            return true;
        }

        _temporarilyUnavailableRoutes.TryRemove(key, out _);
        return false;
    }

    private bool IsRelayTemporarilyUnavailable(SystemUri relayUrl, DateTimeOffset now)
    {
        var key = CreateRelayKey(relayUrl);
        if (!_temporarilyUnavailableRelays.TryGetValue(key, out var failedAt))
        {
            return false;
        }

        if (now - failedAt < _failedRelayCacheDuration)
        {
            return true;
        }

        _temporarilyUnavailableRelays.TryRemove(key, out _);
        return false;
    }

    private void MarkRouteUnavailable(SystemUri directoryUrl, SystemUri relayUrl)
    {
        _temporarilyUnavailableRoutes[CreateRouteKey(directoryUrl, relayUrl)] = DateTimeOffset.UtcNow;
    }

    private static void RemoveRelay(List<SystemUri> remainingRelays, SystemUri relayUrl)
    {
        for (var i = 0; i < remainingRelays.Count; i++)
        {
            if (string.Equals(remainingRelays[i].AbsoluteUri, relayUrl.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
            {
                remainingRelays.RemoveAt(i);
                break;
            }
        }
    }

    private SystemUri? ChooseRelayForBootstrap(SystemUri directoryUrl, IReadOnlyList<SystemUri> remainingRelays, DateTimeOffset now)
    {
        var orderedRelays = OrderRelayUrls(remainingRelays);
        foreach (var relayUrl in orderedRelays)
        {
            if (!IsRouteTemporarilyUnavailable(directoryUrl, relayUrl, now))
            {
                return relayUrl;
            }
        }

        return null;
    }

    private static string CreateRouteKey(SystemUri directoryUrl, SystemUri relayUrl)
    {
        return $"{directoryUrl.AbsoluteUri}|{relayUrl.AbsoluteUri}";
    }

    private static string CreateRelayKey(SystemUri relayUrl)
    {
        return relayUrl.AbsoluteUri;
    }

    private readonly record struct RouteSelectionAttempt(SelectedPayjoinBootstrapRoute? Route, RouteSelectionDisposition Disposition)
    {
        public static RouteSelectionAttempt Selected(SelectedPayjoinBootstrapRoute route) => new(route, RouteSelectionDisposition.ContinueCurrentDirectory);

        public static RouteSelectionAttempt ContinueCurrentDirectory() => new(null, RouteSelectionDisposition.ContinueCurrentDirectory);

        public static RouteSelectionAttempt ContinueNextDirectory() => new(null, RouteSelectionDisposition.ContinueNextDirectory);
    }

    private enum RouteSelectionDisposition
    {
        ContinueCurrentDirectory,
        ContinueNextDirectory
    }
}

internal sealed record SelectedPayjoinBootstrapRoute(SystemUri DirectoryUrl, SystemUri RelayUrl, OhttpKeys OhttpKeys);
