using BTCPayServer.Plugins.Payjoin.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

public interface IPayjoinStoreSettingsRepository
{
    Task<PayjoinStoreSettings?> GetAsync(string storeId);
    Task<IReadOnlyList<(string StoreId, PayjoinStoreSettings Settings)>> GetAllAsync();
    Task SetAsync(string storeId, PayjoinStoreSettings settings);
}
