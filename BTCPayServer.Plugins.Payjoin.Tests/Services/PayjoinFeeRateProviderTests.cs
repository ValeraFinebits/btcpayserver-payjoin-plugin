using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinFeeRateProviderTests
{
    [Fact]
    public async Task GetMaxEffectiveFeeRateUsesTheProvidedStoreSettingsSnapshot()
    {
        var provider = new PayjoinFeeRateProvider(
            feeProviderFactory: null!,
            networkProvider: null!,
            NullLogger<PayjoinFeeRateProvider>.Instance);

        var result = await provider.GetMaxEffectiveFeeRateSatPerVbAsync(
            "store-1",
            storeOverrideSatPerVb: 42,
            TestContext.Current.CancellationToken);

        Assert.Equal(42UL, result);
    }

    [Fact]
    public void ResolveMaxFeeRatePrefersTheStoreOverride()
    {
        Assert.Equal(42UL, PayjoinFeeRateProvider.ResolveMaxFeeRate(42, estimatedSatPerVb: 100m));
    }

    [Fact]
    public void ResolveMaxFeeRateScalesTheEstimateWithHeadroom()
    {
        Assert.Equal(60UL, PayjoinFeeRateProvider.ResolveMaxFeeRate(null, estimatedSatPerVb: 20m));
    }

    [Fact]
    public void ResolveMaxFeeRateRoundsFractionalEstimatesUp()
    {
        Assert.Equal(63UL, PayjoinFeeRateProvider.ResolveMaxFeeRate(null, estimatedSatPerVb: 20.4m));
    }

    [Fact]
    public void ResolveMaxFeeRateNeverDropsBelowTheMinimum()
    {
        Assert.Equal(PayjoinFeeRateProvider.MinimumMaxEffectiveFeeRateSatPerVb, PayjoinFeeRateProvider.ResolveMaxFeeRate(null, estimatedSatPerVb: 1m));
    }

    [Fact]
    public void ResolveMaxFeeRateCapsPathologicalEstimatesWithoutOverflowing()
    {
        Assert.Equal(
            (ulong)PayjoinStoreSettings.MaxFeeRateSatPerVbLimit,
            PayjoinFeeRateProvider.ResolveMaxFeeRate(null, estimatedSatPerVb: decimal.MaxValue));
    }

    [Fact]
    public void ResolveMaxFeeRateCapsPersistedOverridesAtTheSharedLimit()
    {
        Assert.Equal(
            (ulong)PayjoinStoreSettings.MaxFeeRateSatPerVbLimit,
            PayjoinFeeRateProvider.ResolveMaxFeeRate(long.MaxValue, estimatedSatPerVb: null));
    }

    [Fact]
    public void ResolveMaxFeeRateFallsBackWithoutAnEstimate()
    {
        Assert.Equal(PayjoinFeeRateProvider.FallbackMaxEffectiveFeeRateSatPerVb, PayjoinFeeRateProvider.ResolveMaxFeeRate(null, estimatedSatPerVb: null));
        Assert.Equal(PayjoinFeeRateProvider.FallbackMaxEffectiveFeeRateSatPerVb, PayjoinFeeRateProvider.ResolveMaxFeeRate(null, estimatedSatPerVb: 0m));
    }

    [Fact]
    public void ResolveMaxFeeRateIgnoresNonPositiveOverrides()
    {
        Assert.Equal(PayjoinFeeRateProvider.FallbackMaxEffectiveFeeRateSatPerVb, PayjoinFeeRateProvider.ResolveMaxFeeRate(0, estimatedSatPerVb: null));
        Assert.Equal(PayjoinFeeRateProvider.FallbackMaxEffectiveFeeRateSatPerVb, PayjoinFeeRateProvider.ResolveMaxFeeRate(-5, estimatedSatPerVb: null));
    }
}
