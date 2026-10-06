using BTCPayServer.Data;
using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

public sealed class PayjoinStoreSettingsRepository : IPayjoinStoreSettingsRepository
{
    private const string Key = "payjoin.settings";

    private static readonly Action<ILogger, string, Exception?> LogUnreadableSettings =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1, nameof(LogUnreadableSettings)),
            "Payjoin settings for store {StoreId} could not be read and were skipped; re-save them on the store's payjoin page to repair them.");

    private readonly StoreRepository _storeRepository;
    private readonly ILogger<PayjoinStoreSettingsRepository> _logger;

    public PayjoinStoreSettingsRepository(StoreRepository storeRepository, ILogger<PayjoinStoreSettingsRepository> logger)
    {
        _storeRepository = storeRepository;
        _logger = logger;
    }

    public async Task<PayjoinStoreSettings?> GetAsync(string storeId)
    {
        var store = await _storeRepository.FindStore(storeId).ConfigureAwait(false);
        if (store is null)
        {
            return null;
        }

        return ReadSettings(store.GetStoreBlob());
    }

    public async Task<IReadOnlyList<(string StoreId, PayjoinStoreSettings Settings)>> GetAllAsync()
    {
        var stores = await _storeRepository.GetStores().ConfigureAwait(false);
        var results = new List<(string StoreId, PayjoinStoreSettings Settings)>();
        foreach (var store in stores)
        {
            var settings = ReadSettings(store.GetStoreBlob());
            if (settings is null)
            {
                LogUnreadableSettings(_logger, store.Id, null);
                continue;
            }

            results.Add((store.Id, settings));
        }

        return results;
    }

    public async Task SetAsync(string storeId, PayjoinStoreSettings settings)
    {
        if (settings is null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        var normalizedSettings = Normalize(settings);

        var store = await _storeRepository.FindStore(storeId).ConfigureAwait(false);
        if (store is null)
        {
            return;
        }

        var blob = store.GetStoreBlob();
        blob.AdditionalData ??= new JObject();
        blob.AdditionalData[Key] = JToken.FromObject(normalizedSettings);
        _ = store.SetStoreBlob(blob);
        await _storeRepository.UpdateStore(store).ConfigureAwait(false);
    }

    internal static PayjoinStoreSettings Normalize(PayjoinStoreSettings settings)
    {
        var normalizedSettings = new PayjoinStoreSettings
        {
            PayjoinV2Enabled = settings.PayjoinV2Enabled,
            DirectoryUrls = PayjoinStoreSettings.NormalizeDirectoryUrls(settings.DirectoryUrls),
            OhttpRelayUrls = PayjoinStoreSettings.NormalizeOhttpRelayUrls(settings.OhttpRelayUrls),
            ColdWalletDerivationScheme = settings.ColdWalletDerivationScheme,
            MaxFeeRateSatPerVb = settings.MaxFeeRateSatPerVb
        };
        normalizedSettings.NormalizeUrlSettings();
        return normalizedSettings;
    }

    internal static PayjoinStoreSettings? ReadSettings(StoreBlob blob)
    {
        if (blob.AdditionalData is null ||
            !blob.AdditionalData.TryGetValue(Key, out var token) ||
            token is null ||
            token.Type == JTokenType.Null)
        {
            return new PayjoinStoreSettings();
        }

        try
        {
            var settings = token.ToObject<PayjoinStoreSettings>();
            if (settings is null)
            {
                return new PayjoinStoreSettings();
            }

            settings.NormalizeUrlSettings();
            return settings;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
