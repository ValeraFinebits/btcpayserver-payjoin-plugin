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
        Assert.NotNull(schemas["PayjoinStoreSettingsData"]);
        Assert.NotNull(schemas["PayjoinPaymentUrlData"]);
    }
}
