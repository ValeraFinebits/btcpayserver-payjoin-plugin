using BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services.Wallets;
using BTCPayServer.Tests;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class PayjoinWalletOwnershipIntegrationTests : UnitTestBase
{
    public PayjoinWalletOwnershipIntegrationTests(ITestOutputHelper helper) : base(helper)
    {
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task ResolverRecognizesWalletOutpointAfterItLeavesTheUtxoSet()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(
            tester,
            initialFundingUtxoCount: 1,
            cancellationToken: cts.Token).ConfigureAwait(true);

        var wallet = tester.PayTester.GetService<BTCPayWalletProvider>().GetWallet(context.Network);
        var coin = Assert.Single(await wallet.GetUnspentCoins(
            context.Merchant.DerivationScheme,
            excludeUnconfirmed: true,
            cts.Token).ConfigureAwait(true));

        using var destinationKey = new Key();
        var explorerClient = tester.PayTester.GetService<ExplorerClientProvider>().GetExplorerClient(context.Network);
        var spendPsbt = (await explorerClient.CreatePSBTAsync(
            context.Merchant.DerivationScheme,
            new CreatePSBTRequest
            {
                IncludeOnlyOutpoints = [coin.OutPoint],
                FeePreference = new FeePreference
                {
                    ExplicitFeeRate = new FeeRate(5m)
                },
                Destinations =
                {
                    new CreatePSBTDestination
                    {
                        Destination = destinationKey.PubKey.WitHash.GetAddress(context.Network.NBitcoinNetwork),
                        Amount = Money.Coins(0.5m)
                    }
                }
            },
            cts.Token).ConfigureAwait(true)).PSBT;
        spendPsbt = await context.Merchant.Sign(spendPsbt).ConfigureAwait(true);
        spendPsbt.Finalize();

        var broadcast = await explorerClient.BroadcastAsync(spendPsbt.ExtractTransaction(), cts.Token).ConfigureAwait(true);
        Assert.True(broadcast.Success, broadcast.RPCMessage);
        await tester.ExplorerNode.GenerateAsync(1, cts.Token).ConfigureAwait(true);

        await Tests.TestUtils.EventuallyAsync(async () =>
        {
            wallet.InvalidateCache(context.Merchant.DerivationScheme);
            var currentCoins = await wallet.GetUnspentCoins(
                context.Merchant.DerivationScheme,
                excludeUnconfirmed: false,
                cts.Token).ConfigureAwait(true);
            Assert.DoesNotContain(currentCoins, candidate => candidate.OutPoint == coin.OutPoint);
        }).ConfigureAwait(true);

        var fundingTransaction = await explorerClient.GetTransactionAsync(coin.OutPoint.Hash, cts.Token).ConfigureAwait(true);
        Assert.NotNull(fundingTransaction);
        var otherOutputIndexes = Enumerable
            .Range(0, fundingTransaction!.Transaction.Outputs.Count)
            .Select(index => (uint)index)
            .Where(index => index != coin.OutPoint.N)
            .ToArray();
        Assert.NotEmpty(otherOutputIndexes);

        var otherOutputFromWalletTransaction = new OutPoint(coin.OutPoint.Hash, otherOutputIndexes[0]);
        var foreignOutpoint = new OutPoint(uint256.One, 0);
        var ownershipService = tester.PayTester.GetService<IPayjoinWalletOwnershipService>();
        var resolver = await ownershipService.CreateInputResolverAsync(
            context.Merchant.StoreId,
            [coin.OutPoint, otherOutputFromWalletTransaction, foreignOutpoint],
            cts.Token).ConfigureAwait(true);

        Assert.True(resolver.IsOwned(coin.OutPoint.Hash.ToString(), coin.OutPoint.N));
        Assert.False(resolver.IsOwned(otherOutputFromWalletTransaction.Hash.ToString(), otherOutputFromWalletTransaction.N));
        Assert.False(resolver.IsOwned(foreignOutpoint.Hash.ToString(), foreignOutpoint.N));
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task ResolverRecognizesUnconfirmedColdWalletOutpoint()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(
            tester,
            cancellationToken: cts.Token).ConfigureAwait(true);
        var coldDerivation = await PayjoinIntegrationTestSupport.CreateTrackedColdWalletAsync(
            tester,
            cts.Token).ConfigureAwait(true);
        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, settings =>
        {
            settings.ColdWalletDerivationScheme = coldDerivation.ToString();
        }, cts.Token).ConfigureAwait(true);

        var explorerClient = tester.PayTester.GetService<ExplorerClientProvider>().GetExplorerClient(context.Network);
        var coldAddress = await explorerClient.GetUnusedAsync(
            coldDerivation,
            DerivationFeature.Deposit,
            0,
            true,
            cts.Token).ConfigureAwait(true);
        Assert.NotNull(coldAddress);
        var coldDestination = coldAddress!.ScriptPubKey.GetDestinationAddress(context.Network.NBitcoinNetwork);
        Assert.NotNull(coldDestination);
        var fundingTransactionId = await tester.ExplorerNode.SendToAddressAsync(
            coldDestination!,
            Money.Coins(0.1m),
            cancellationToken: cts.Token).ConfigureAwait(true);

        OutPoint? coldOutpoint = null;
        await Tests.TestUtils.EventuallyAsync(async () =>
        {
            var coldUtxos = await explorerClient.GetUTXOsAsync(coldDerivation, cts.Token).ConfigureAwait(true);
            var coldUtxo = coldUtxos.GetUnspentUTXOs(false)
                .SingleOrDefault(candidate => candidate.Outpoint.Hash == fundingTransactionId);
            Assert.NotNull(coldUtxo);
            Assert.Equal(0, coldUtxo!.Confirmations);
            coldOutpoint = coldUtxo.Outpoint;
        }).ConfigureAwait(true);

        var ownershipService = tester.PayTester.GetService<IPayjoinWalletOwnershipService>();
        var resolver = await ownershipService.CreateInputResolverAsync(
            context.Merchant.StoreId,
            [coldOutpoint!],
            cts.Token).ConfigureAwait(true);

        Assert.True(resolver.IsOwned(coldOutpoint!.Hash.ToString(), coldOutpoint.N));
    }
}
