using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Services;
using Microsoft.Extensions.Logging;
using NBitcoin;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

/// <summary>
/// Resolves the maximum effective fee rate a receiver session is willing to reach. A per-store
/// override wins; otherwise the platform's fee estimation drives the cap so it tracks the network
/// instead of sitting at a fixed ceiling, bounding how far a sender's requested fee rate can pull
/// the receiver's own contribution.
/// </summary>
internal interface IPayjoinFeeRateProvider
{
    Task<ulong> GetMaxEffectiveFeeRateSatPerVbAsync(string storeId, CancellationToken cancellationToken);
}

internal sealed class PayjoinFeeRateProvider : IPayjoinFeeRateProvider
{
    // The previous fixed cap, kept as the behavior-preserving fallback when no estimate is available.
    internal const ulong FallbackMaxEffectiveFeeRateSatPerVb = 1000;

    // Headroom over the next-block estimate: senders legitimately pay above the estimate, and the
    // receiver only bears the extra fee on its own contribution, so a generous multiple avoids
    // failing honest payjoins while still tracking the prevailing rate.
    internal const int EstimateSafetyMultiplier = 3;

    // A quiet mempool can estimate 1 sat/vB; refusing every sender above 3 sat/vB would fail
    // payjoins needlessly, so the estimate-driven cap never drops below this.
    internal const ulong MinimumMaxEffectiveFeeRateSatPerVb = 25;

    private const int EstimationBlockTarget = 1;

    private static readonly Action<ILogger, string, Exception?> LogEstimationUnavailable =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(1, nameof(LogEstimationUnavailable)),
            "Payjoin fee estimation unavailable for store {StoreId}; using the fallback maximum fee rate.");

    private readonly IPayjoinStoreSettingsRepository _storeSettingsRepository;
    private readonly IFeeProviderFactory _feeProviderFactory;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly ILogger<PayjoinFeeRateProvider> _logger;

    public PayjoinFeeRateProvider(
        IPayjoinStoreSettingsRepository storeSettingsRepository,
        IFeeProviderFactory feeProviderFactory,
        BTCPayNetworkProvider networkProvider,
        ILogger<PayjoinFeeRateProvider> logger)
    {
        _storeSettingsRepository = storeSettingsRepository;
        _feeProviderFactory = feeProviderFactory;
        _networkProvider = networkProvider;
        _logger = logger;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Fee estimation failing for any reason must degrade to the fallback cap, not break session creation.")]
    public async Task<ulong> GetMaxEffectiveFeeRateSatPerVbAsync(string storeId, CancellationToken cancellationToken)
    {
        var settings = await _storeSettingsRepository.GetAsync(storeId).ConfigureAwait(false);
        var storeOverrideSatPerVb = settings?.MaxFeeRateSatPerVb;
        decimal? estimatedSatPerVb = null;
        if (storeOverrideSatPerVb is null or <= 0)
        {
            try
            {
                var network = _networkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode);
                if (network is not null)
                {
                    var feeProvider = _feeProviderFactory.CreateFeeProvider(network);
                    var feeRate = await feeProvider.GetFeeRateAsync(EstimationBlockTarget).ConfigureAwait(false);
                    estimatedSatPerVb = feeRate.SatoshiPerByte;
                }
            }
            catch (Exception ex)
            {
                LogEstimationUnavailable(_logger, storeId, ex);
            }
        }

        return ResolveMaxFeeRate(storeOverrideSatPerVb, estimatedSatPerVb);
    }

    internal static ulong ResolveMaxFeeRate(long? storeOverrideSatPerVb, decimal? estimatedSatPerVb)
    {
        if (storeOverrideSatPerVb is > 0)
        {
            return (ulong)Math.Min(storeOverrideSatPerVb.Value, PayjoinStoreSettings.MaxFeeRateSatPerVbLimit);
        }

        if (estimatedSatPerVb is > 0m)
        {
            // Clamp before multiplying: decimal.MaxValue * EstimateSafetyMultiplier would overflow
            // before a final clamp could run. The result uses the same ceiling as the settings form
            // and API, so even a pathological provider result remains a valid session cap.
            var roundedEstimate = Math.Ceiling(estimatedSatPerVb.Value);
            var boundedEstimate = Math.Min(roundedEstimate, PayjoinStoreSettings.MaxFeeRateSatPerVbLimit);
            var scaled = boundedEstimate * EstimateSafetyMultiplier;
            var bounded = Math.Clamp(scaled, MinimumMaxEffectiveFeeRateSatPerVb, PayjoinStoreSettings.MaxFeeRateSatPerVbLimit);
            return (ulong)bounded;
        }

        return FallbackMaxEffectiveFeeRateSatPerVb;
    }
}
