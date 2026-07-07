using BTCPayServer.Plugins.Payjoin.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class PayjoinSwaggerProviderTests
{
    [Fact]
    public async Task FetchDocumentsPayjoinGreenfieldEndpoints()
    {
        var swagger = await new PayjoinSwaggerProvider().Fetch();

        var paths = Assert.IsType<JObject>(swagger["paths"]);
        Assert.NotNull(paths["/api/v1/stores/{storeId}/payjoin/settings"]?["get"]);
        Assert.NotNull(paths["/api/v1/stores/{storeId}/payjoin/settings"]?["put"]);
        Assert.NotNull(paths["/api/v1/stores/{storeId}/invoices/{invoiceId}/payjoin/payment-url"]?["get"]);

        var schemas = Assert.IsType<JObject>(swagger["components"]?["schemas"]);
        var payjoinSettingsSchema = Assert.IsType<JObject>(schemas["PayjoinStoreSettingsData"]);
        var properties = Assert.IsType<JObject>(payjoinSettingsSchema["properties"]);
        Assert.NotNull(properties["directoryUrls"]);
        Assert.NotNull(properties["ohttpRelayUrls"]);
        Assert.Null(properties["directoryUrlsText"]);
        Assert.Null(properties["ohttpRelayUrlsText"]);
        Assert.NotNull(schemas["PayjoinPaymentUrlData"]);
    }
}
