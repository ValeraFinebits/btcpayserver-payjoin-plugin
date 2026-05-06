using System.Threading;
using System.Threading.Tasks;
using SystemUri = System.Uri;

namespace BTCPayServer.Plugins.Payjoin.Services;

public interface IPayjoinReceiverRelayClient
{
    Task<byte[]> SendAsync(SystemUri url, string contentType, byte[] body, CancellationToken cancellationToken);
}
