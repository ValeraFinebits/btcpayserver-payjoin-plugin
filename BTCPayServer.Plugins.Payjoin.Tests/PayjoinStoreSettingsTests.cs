using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.Payjoin.Models;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class PayjoinStoreSettingsTests
{
    private const string TestXpub = "xpub661MyMwAqRbcFtXgS5sYJABqqG9YLmC4Q1Rdap9gSE8NqtwybGhePY2gZ29ESFjqJoCu1Rupje8YtGqsefD265TMg7usUDFdp6W1EGMcet8";

    [Fact]
    public void NewSettingsHasExpectedDefaults()
    {
        var settings = new PayjoinStoreSettings();

        Assert.True(settings.PayjoinV2Enabled);
        Assert.Equal(PayjoinStoreSettings.DefaultDirectoryUrl, settings.DirectoryUrl);
        Assert.Equal(PayjoinStoreSettings.DefaultOhttpRelayUrl, settings.OhttpRelayUrl);
        Assert.Null(settings.ColdWalletDerivationScheme);
    }

    [Fact]
    public void SettingsPreserveAssignedValues()
    {
        var directoryUrl = new Uri("https://example.com/directory");
        var ohttpRelayUrl = new Uri("https://example.com/relay");

        var settings = new PayjoinStoreSettings
        {
            PayjoinV2Enabled = true,
            DirectoryUrl = directoryUrl,
            OhttpRelayUrl = ohttpRelayUrl,
            ColdWalletDerivationScheme = TestXpub
        };

        Assert.True(settings.PayjoinV2Enabled);
        Assert.Equal(directoryUrl, settings.DirectoryUrl);
        Assert.Equal(ohttpRelayUrl, settings.OhttpRelayUrl);
        Assert.Equal(TestXpub, settings.ColdWalletDerivationScheme);
    }

    [Fact]
    public void DataRoundTripsThroughSettings()
    {
        var data = new PayjoinStoreSettingsData
        {
            PayjoinV2Enabled = false,
            DirectoryUrl = new Uri("https://example.com/directory"),
            OhttpRelayUrl = new Uri("https://example.com/relay"),
            ColdWalletDerivationScheme = TestXpub
        };

        var settings = data.ToSettings();
        var roundTripped = PayjoinStoreSettingsData.FromSettings(settings);

        Assert.Equal(data.PayjoinV2Enabled, roundTripped.PayjoinV2Enabled);
        Assert.Equal(data.DirectoryUrl, roundTripped.DirectoryUrl);
        Assert.Equal(data.OhttpRelayUrl, roundTripped.OhttpRelayUrl);
        Assert.Equal(data.ColdWalletDerivationScheme, roundTripped.ColdWalletDerivationScheme);
    }

    [Fact]
    public void ViewModelRoundTripsThroughSettings()
    {
        var layoutModel = new LayoutModel("Payjoin", "Payjoin");
        var model = new PayjoinStoreSettingsViewModel
        {
            StoreId = "store-1",
            PayjoinV2Enabled = false,
            DirectoryUrl = new Uri("https://example.com/directory"),
            OhttpRelayUrl = new Uri("https://example.com/relay"),
            ColdWalletDerivationScheme = TestXpub,
            LayoutModel = layoutModel
        };

        var settings = model.ToSettings();
        var roundTripped = PayjoinStoreSettingsViewModel.FromSettings(model.StoreId, settings, layoutModel);

        Assert.Equal(model.StoreId, roundTripped.StoreId);
        Assert.Equal(model.PayjoinV2Enabled, roundTripped.PayjoinV2Enabled);
        Assert.Equal(model.DirectoryUrl, roundTripped.DirectoryUrl);
        Assert.Equal(model.OhttpRelayUrl, roundTripped.OhttpRelayUrl);
        Assert.Equal(model.ColdWalletDerivationScheme, roundTripped.ColdWalletDerivationScheme);
        Assert.Same(layoutModel, roundTripped.LayoutModel);
    }
}
