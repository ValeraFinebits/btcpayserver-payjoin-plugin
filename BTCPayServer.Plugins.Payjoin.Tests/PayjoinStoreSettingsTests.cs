using BTCPayServer.Plugins.Payjoin.Models;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class PayjoinStoreSettingsTests
{
    [Fact]
    public void NewSettingsHasExpectedDefaults()
    {
        var settings = new PayjoinStoreSettings();

        Assert.False(settings.EnabledByDefault);
        Assert.False(settings.DemoMode);
        Assert.Null(settings.DirectoryUrl);
        Assert.Null(settings.OhttpRelayUrl);
    }

    [Fact]
    public void SettingsPreserveAssignedValues()
    {
        var directoryUrl = new Uri("https://example.com/directory");
        var ohttpRelayUrl = new Uri("https://example.com/relay");

        var settings = new PayjoinStoreSettings
        {
            EnabledByDefault = true,
            DemoMode = true,
            DirectoryUrl = directoryUrl,
            OhttpRelayUrl = ohttpRelayUrl
        };

        Assert.True(settings.EnabledByDefault);
        Assert.True(settings.DemoMode);
        Assert.Equal(directoryUrl, settings.DirectoryUrl);
        Assert.Equal(ohttpRelayUrl, settings.OhttpRelayUrl);
    }
}
