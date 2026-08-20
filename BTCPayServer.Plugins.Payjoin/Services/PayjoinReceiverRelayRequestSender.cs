using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using SystemUri = System.Uri;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal sealed class PayjoinReceiverRelayRequestSender : IPayjoinReceiverRelayRequestSender
{
    private readonly IPayjoinStoreSettingsRepository _storeSettingsRepository;
    private readonly PayjoinMailroomManager _mailroomManager;
    private readonly IPayjoinReceiverRelayClient _relayClient;

    public PayjoinReceiverRelayRequestSender(
        IPayjoinStoreSettingsRepository storeSettingsRepository,
        PayjoinMailroomManager mailroomManager,
        IPayjoinReceiverRelayClient relayClient)
    {
        _storeSettingsRepository = storeSettingsRepository;
        _mailroomManager = mailroomManager;
        _relayClient = relayClient;
    }

    public async Task<(byte[] ResponseBody, TRequestContext RequestContext)> SendAsync<TRequestContext>(
        string storeId,
        string sessionId,
        Func<string, TRequestContext> buildRequest,
        Func<TRequestContext, (SystemUri Url, string ContentType, byte[] Body)> describeRequest,
        CancellationToken cancellationToken)
        where TRequestContext : IDisposable
    {
        ArgumentNullException.ThrowIfNull(storeId);
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(buildRequest);
        ArgumentNullException.ThrowIfNull(describeRequest);

        var storeSettings = await _storeSettingsRepository.GetAsync(storeId).ConfigureAwait(false);
        if (storeSettings is null)
        {
            throw new PayjoinStoreSettingsUnavailableException(
                $"Payjoin settings for store '{storeId}' could not be read.");
        }

        var relayUrls = storeSettings.GetEffectiveOhttpRelayUrls();
        if (relayUrls.Count == 0)
        {
            throw new InvalidOperationException($"No OHTTP relay URLs are configured for payjoin store '{storeId}'.");
        }

        Exception? lastTransportError = null;
        var remainingRelays = new List<SystemUri>(relayUrls);
        while (remainingRelays.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relayUrl = _mailroomManager.ChooseRelayForRequest(remainingRelays);
            if (relayUrl is null)
            {
                break;
            }

            var requestContext = buildRequest(relayUrl.AbsoluteUri);
            try
            {
                var (url, contentType, body) = describeRequest(requestContext);
                var responseBody = await _relayClient.SendAsync(url, contentType, body, cancellationToken).ConfigureAwait(false);
                return (responseBody, requestContext);
            }
            catch (PayjoinReceiverRelayTimeoutException ex)
            {
                requestContext.Dispose();
                _mailroomManager.MarkRelayTemporarilyUnavailable(relayUrl);
                RemoveRelay(remainingRelays, relayUrl);
                lastTransportError = ex;
            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                requestContext.Dispose();
                _mailroomManager.MarkRelayTemporarilyUnavailable(relayUrl);
                RemoveRelay(remainingRelays, relayUrl);
                lastTransportError = ex;
            }
            catch
            {
                requestContext.Dispose();
                throw;
            }
        }

        if (lastTransportError is not null)
        {
            ExceptionDispatchInfo.Capture(lastTransportError).Throw();
        }

        throw new PayjoinReceiverRelayTimeoutException($"No configured OHTTP relays are currently available for payjoin session '{sessionId}'.");
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
}
