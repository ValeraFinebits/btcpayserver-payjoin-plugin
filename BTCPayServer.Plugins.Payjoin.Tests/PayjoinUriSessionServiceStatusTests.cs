using BTCPayServer.Logging;
using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NBXplorer;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class PayjoinUriSessionServiceStatusTests
{
    private const string Destination = "bcrt1qexampledestination";
    private static readonly Uri[] SampleUrls = [new("https://configured.example/endpoint")];

    private static PayjoinUriSessionService CreateService()
    {
        return new PayjoinUriSessionService(
            CreateNetworkProvider(),
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            NullLogger<PayjoinUriSessionService>.Instance);
    }

    private static Task<PayjoinUriResult> BuildAsync(
        PayjoinStoreSettings? storeSettings,
        bool enablePayjoin = true,
        decimal due = 0.1m)
    {
        return CreateService().BuildAsync(
            PayjoinConstants.BitcoinCode,
            Destination,
            due,
            storeSettings,
            enablePayjoin,
            "invoice-1",
            "store-1",
            DateTimeOffset.UtcNow.AddHours(1),
            TestContext.Current.CancellationToken);
    }

    private static PayjoinStoreSettings CreateSettings(Uri[]? directoryUrls = null, Uri[]? ohttpRelayUrls = null)
    {
        return new PayjoinStoreSettings
        {
            PayjoinV2Enabled = true,
            DirectoryUrls = directoryUrls ?? SampleUrls,
            OhttpRelayUrls = ohttpRelayUrls ?? SampleUrls
        };
    }

    private static void AssertPlainBip21Fallback(PayjoinUriResult result, PayjoinAvailabilityStatus expectedStatus, string expectedReason)
    {
        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedReason, result.Reason);
        Assert.DoesNotContain("pj=", result.PaymentUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DisabledStoreSettingMapsToDisabledByStore()
    {
        var result = await BuildAsync(CreateSettings(), enablePayjoin: false);

        AssertPlainBip21Fallback(result, PayjoinAvailabilityStatus.DisabledByStore, PayjoinUnavailableReasons.DisabledByStoreSettings);
    }

    [Fact]
    public async Task UnreadableStoreSettingsAreTemporaryButNotRetryable()
    {
        var result = await BuildAsync(storeSettings: null);

        AssertPlainBip21Fallback(result, PayjoinAvailabilityStatus.TemporarilyUnavailable, PayjoinUnavailableReasons.StoreSettingsUnavailable);
        Assert.False(result.Retryable);
    }

    [Fact]
    public async Task MissingDirectoryUrlsMapToMerchantRequirementsUnmet()
    {
        var result = await BuildAsync(CreateSettings(directoryUrls: []));

        AssertPlainBip21Fallback(result, PayjoinAvailabilityStatus.MerchantRequirementsUnmet, PayjoinUnavailableReasons.DirectoryUrlsMissing);
    }

    [Fact]
    public async Task MissingOhttpRelayUrlsMapToMerchantRequirementsUnmet()
    {
        var result = await BuildAsync(CreateSettings(ohttpRelayUrls: []));

        AssertPlainBip21Fallback(result, PayjoinAvailabilityStatus.MerchantRequirementsUnmet, PayjoinUnavailableReasons.OhttpRelayUrlsMissing);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.5)]
    public async Task NonPositiveDueMapsToInvoiceNotPayable(decimal due)
    {
        var result = await BuildAsync(CreateSettings(), due: due);

        AssertPlainBip21Fallback(result, PayjoinAvailabilityStatus.InvoiceNotPayable, PayjoinUnavailableReasons.InvoiceAmountNotPositive);
    }

    private static BTCPayNetworkProvider CreateNetworkProvider()
    {
        var nbxplorerNetworkProvider = new NBXplorerNetworkProvider(ChainName.Regtest);
        var network = new BTCPayNetwork
        {
            CryptoCode = PayjoinConstants.BitcoinCode,
            DisplayName = "Bitcoin",
            NBXplorerNetwork = nbxplorerNetworkProvider.GetFromCryptoCode(PayjoinConstants.BitcoinCode),
            CryptoImagePath = "imlegacy/bitcoin.svg",
            LightningImagePath = "imlegacy/bitcoin-lightning.svg",
            DefaultSettings = new BTCPayDefaultSettings(),
            CoinType = new KeyPath("1'"),
            SupportRBF = true,
            SupportPayJoin = true,
            VaultSupported = true
        }.SetDefaultElectrumMapping(ChainName.Regtest);

        return new BTCPayNetworkProvider([network], nbxplorerNetworkProvider, new Logs());
    }
}
