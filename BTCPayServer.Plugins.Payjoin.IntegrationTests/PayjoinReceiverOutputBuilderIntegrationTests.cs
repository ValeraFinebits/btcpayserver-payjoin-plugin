using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Tests;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class PayjoinReceiverOutputBuilderIntegrationTests : UnitTestBase
{
    private const long SettlementAmountSats = 50_000;

    public PayjoinReceiverOutputBuilderIntegrationTests(ITestOutputHelper helper) : base(helper)
    {
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task GetsSettlementPathFromRealHotWalletChangeReservation()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper
            .CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token)
            .ConfigureAwait(true);

        var outputBuilder = tester.PayTester.GetService<IPayjoinReceiverOutputBuilder>();
        using var receiverKey = new Key();
        var receiverScript = receiverKey.PubKey.WitHash.ScriptPubKey.ToBytes();

        var result = await outputBuilder.TryCreateSettlementOutputsAsync(
            context.Merchant.StoreId,
            "hot-wallet-path-test",
            receiverScript,
            preserveReceiverScript: false,
            pinnedSettlementAmountSats: SettlementAmountSats,
            cancellationToken: cts.Token).ConfigureAwait(true);

        Assert.NotNull(result);
        Assert.Equal<uint>(1, result!.SettlementKeyPath.Indexes[0]);
        await AssertNbxplorerReturnedMatchingPathAsync(
            tester,
            context.Merchant.DerivationScheme,
            result,
            cts.Token).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task GetsSettlementPathFromRealColdWalletChangeReservation()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper
            .CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token)
            .ConfigureAwait(true);
        var coldDerivation = await PayjoinIntegrationTestSupport
            .CreateTrackedColdWalletAsync(tester, cts.Token)
            .ConfigureAwait(true);
        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, settings =>
        {
            settings.ColdWalletDerivationScheme = coldDerivation.ToString();
        }, cts.Token).ConfigureAwait(true);

        var outputBuilder = tester.PayTester.GetService<IPayjoinReceiverOutputBuilder>();
        using var receiverKey = new Key();
        var receiverScript = receiverKey.PubKey.WitHash.ScriptPubKey.ToBytes();

        var result = await outputBuilder.TryCreateSettlementOutputsAsync(
            context.Merchant.StoreId,
            "cold-wallet-path-test",
            receiverScript,
            preserveReceiverScript: false,
            pinnedSettlementAmountSats: SettlementAmountSats,
            cancellationToken: cts.Token).ConfigureAwait(true);

        Assert.NotNull(result);
        Assert.Equal<uint>(1, result!.SettlementKeyPath.Indexes[0]);
        await AssertNbxplorerReturnedMatchingPathAsync(
            tester,
            coldDerivation,
            result,
            cts.Token).ConfigureAwait(true);

        var settlementScript = Script.FromBytesUnsafe(result.SettlementScript);
        var hotWalletKeyInformation = await tester.ExplorerClient
            .GetKeyInformationAsync(context.Merchant.DerivationScheme, settlementScript, cts.Token)
            .ConfigureAwait(true);
        Assert.Null(hotWalletKeyInformation);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task DisabledOutputSubstitutionPreservesInvoicePathWithoutReservingChange()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper
            .CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token)
            .ConfigureAwait(true);
        await PayjoinIntegrationTestSupport
            .EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token)
            .ConfigureAwait(true);
        var invoiceContext = await PayjoinInvoiceTestHelper
            .PreparePayjoinInvoiceAsync(tester, context.Merchant, context.Network, cts.Token)
            .ConfigureAwait(true);

        var invoiceRepository = tester.PayTester.GetService<InvoiceRepository>();
        var invoice = await invoiceRepository.GetInvoice(invoiceContext.InvoiceId)
            .WaitAsync(cts.Token)
            .ConfigureAwait(true);
        Assert.NotNull(invoice);
        var prompt = invoice!.GetPaymentPrompt(invoiceContext.PaymentMethodId);
        Assert.NotNull(prompt);
        var handlers = tester.PayTester.GetService<PaymentMethodHandlerDictionary>();
        var promptDetails = Assert.IsType<BitcoinPaymentPromptDetails>(handlers.ParsePaymentPromptDetails(prompt!));
        Assert.NotNull(promptDetails.KeyPath);

        var changeBefore = await tester.ExplorerClient.GetUnusedAsync(
            context.Merchant.DerivationScheme,
            DerivationFeature.Change,
            0,
            reserve: false,
            cts.Token).ConfigureAwait(true);
        Assert.NotNull(changeBefore);

        var outputBuilder = tester.PayTester.GetService<IPayjoinReceiverOutputBuilder>();
        var result = await outputBuilder.TryCreateSettlementOutputsAsync(
            context.Merchant.StoreId,
            invoiceContext.InvoiceId,
            invoiceContext.InvoiceScript.ToBytes(),
            preserveReceiverScript: true,
            pinnedSettlementAmountSats: null,
            cancellationToken: cts.Token).ConfigureAwait(true);

        Assert.NotNull(result);
        Assert.Equal(invoiceContext.InvoiceScript.ToBytes(), result!.SettlementScript);
        Assert.Equal(promptDetails.KeyPath, result.SettlementKeyPath);
        Assert.Equal<uint>(0, result.SettlementKeyPath.Indexes[0]);
        Assert.Equal(
            checked((ulong)Money.Coins(invoiceContext.ExpectedDue).Satoshi),
            result.SettlementAmountSats);

        var changeAfter = await tester.ExplorerClient.GetUnusedAsync(
            context.Merchant.DerivationScheme,
            DerivationFeature.Change,
            0,
            reserve: false,
            cts.Token).ConfigureAwait(true);
        Assert.NotNull(changeAfter);
        Assert.Equal(changeBefore!.KeyPath, changeAfter!.KeyPath);
        Assert.Equal(changeBefore.ScriptPubKey, changeAfter.ScriptPubKey);
    }

    private static async Task AssertNbxplorerReturnedMatchingPathAsync(
        ServerTester tester,
        DerivationStrategyBase derivation,
        PayjoinReceiverOutputBuilder.OutputReplacement result,
        CancellationToken cancellationToken)
    {
        Assert.NotEmpty(result.SettlementKeyPath.Indexes);
        var settlementScript = Script.FromBytesUnsafe(result.SettlementScript);
        var standardDerivation = Assert.IsAssignableFrom<StandardDerivationStrategyBase>(derivation);
        Assert.Equal(
            standardDerivation.GetDerivation(result.SettlementKeyPath).ScriptPubKey,
            settlementScript);

        var keyInformation = await tester.ExplorerClient
            .GetKeyInformationAsync(derivation, settlementScript, cancellationToken)
            .ConfigureAwait(true);
        Assert.NotNull(keyInformation);
        Assert.Equal(result.SettlementKeyPath, keyInformation!.KeyPath);
    }
}
