using BTCPayServer.Data;
using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Services.Stores;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

public sealed class PayjoinStoreSettingsRepository : IPayjoinStoreSettingsRepository
{
    private const string Key = "payjoin.settings";

    private readonly StoreRepository _storeRepository;

    public PayjoinStoreSettingsRepository(StoreRepository storeRepository)
    {
        _storeRepository = storeRepository;
    }

    public async Task<PayjoinStoreSettings> GetAsync(string storeId)
    {
        var store = await _storeRepository.FindStore(storeId).ConfigureAwait(false);
        if (store is null)
        {
            return new PayjoinStoreSettings();
        }

        return ReadSettings(store.GetStoreBlob());
    }

    public async Task<IReadOnlyList<(string StoreId, PayjoinStoreSettings Settings)>> GetAllAsync()
    {
        var stores = await _storeRepository.GetStores().ConfigureAwait(false);
        var results = new List<(string StoreId, PayjoinStoreSettings Settings)>();
        foreach (var store in stores)
        {
            results.Add((store.Id, ReadSettings(store.GetStoreBlob())));
        }

        return results;
    }

    public async Task SetAsync(string storeId, PayjoinStoreSettings settings)
    {
        if (settings is null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        var store = await _storeRepository.FindStore(storeId).ConfigureAwait(false);
        if (store is null)
        {
            return;
        }

        var blob = store.GetStoreBlob();
        blob.AdditionalData ??= new Newtonsoft.Json.Linq.JObject();
        blob.AdditionalData[Key] = Newtonsoft.Json.Linq.JToken.FromObject(settings);
        store.SetStoreBlob(blob);
        await _storeRepository.UpdateStore(store).ConfigureAwait(false);
    }

    internal static PayjoinStoreSettings ReadSettings(StoreBlob blob)
    {
        if (blob.AdditionalData is null || !blob.AdditionalData.TryGetValue(Key, out var token) || token is null)
        {
            return new PayjoinStoreSettings();
        }

        try
        {
            return token.ToObject<PayjoinStoreSettings>() ?? new PayjoinStoreSettings();
        }
        catch (JsonException)
        {
            return new PayjoinStoreSettings();
        }
    }
}
