using BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;
using BTCPayServer.Tests;
using Xunit;
using Xunit.Abstractions;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class PayjoinCliIntegrationTests : UnitTestBase
{
    private static readonly TimeSpan CliTestTimeout = TimeSpan.FromMinutes(3);

    public PayjoinCliIntegrationTests(ITestOutputHelper helper) : base(helper)
    {
    }

    [Fact
    (Skip = "Manual payjoin-cli integration test. Remove Skip to run it explicitly.")
    ]
    [Trait("Integration", "Integration")]
    public async Task CreateInvoiceAndPayItThroughThePayjoinPluginWithPayjoinCli()
    {
        using var cts = new CancellationTokenSource(CliTestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var payjoinCliOptions = new BitcoindNodeOptions();
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.User.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var paymentResult = await PayjoinCliIntegrationTestSupport.CreateAndPayInvoiceWithInvoiceIdAsync(tester, context.User, context.Network, payjoinCliOptions, cts.Token).ConfigureAwait(true);

        PayjoinIntegrationTestSupport.AssertSuccessfulPayjoinTransaction((paymentResult.PayjoinTransaction, paymentResult.InvoiceScript, paymentResult.TransactionId));
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyRemovedAsync(tester, paymentResult.InvoiceId, cts.Token).ConfigureAwait(true);
    }
}
