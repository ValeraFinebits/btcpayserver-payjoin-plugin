using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

public interface IPayjoinReceiverSessionProcessor
{
    Task ProcessTickAsync(CancellationToken stoppingToken);
}
