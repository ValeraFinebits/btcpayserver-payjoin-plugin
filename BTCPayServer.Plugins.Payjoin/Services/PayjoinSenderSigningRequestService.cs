using BTCPayServer.Abstractions;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using Microsoft.Extensions.Logging;
using NBitcoin;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal sealed class PayjoinSenderSigningRequestService(
    BTCPayNetworkProvider networks,
    ApplicationDbContextFactory coreFactory,
    EventAggregator events,
    ILogger<PendingTransactionService> logger)
{
    internal async Task<string?> CreateAsync(PayjoinSenderSessionStore store, PayjoinSenderSessionState session,
        PSBT proposal, RequestBaseUrl baseUrl, CancellationToken cancellationToken)
    {
        // Core publishes Created before returning. Buffer it until both rows commit, so a
        // rolled-back request cannot send a signing email or webhook for a nonexistent row.
        using var bufferedEvents = new EventAggregator(events.Logs);
        PendingTransactionService.PendingTransactionEvent? created = null;
        using var subscription = bufferedEvents.Subscribe<PendingTransactionService.PendingTransactionEvent>(e => created = e);
        var creator = new PendingTransactionService(networks, coreFactory, bufferedEvents, logger);
        var id = await store.CreateSigningRequestAsync(session.SenderSessionId, async () =>
            (await creator.CreatePendingTransaction(session.StoreId, PayjoinConstants.BitcoinCode, proposal, baseUrl,
                DateTimeOffset.UtcNow + PayjoinSenderService.SignatureWindow, cancellationToken).ConfigureAwait(false)).Id).ConfigureAwait(false);
        if (id is not null && created is not null)
            events.Publish(created);
        return id;
    }
}
