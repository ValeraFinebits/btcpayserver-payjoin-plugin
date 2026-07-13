using System;
using System.Threading;
using System.Threading.Tasks;
using SystemUri = System.Uri;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal interface IPayjoinReceiverRelayRequestSender
{
    Task<(byte[] ResponseBody, TRequestContext RequestContext)> SendAsync<TRequestContext>(
        string storeId,
        string invoiceId,
        Func<string, TRequestContext> buildRequest,
        Func<TRequestContext, (SystemUri Url, string ContentType, byte[] Body)> describeRequest,
        CancellationToken cancellationToken)
        where TRequestContext : IDisposable;
}
