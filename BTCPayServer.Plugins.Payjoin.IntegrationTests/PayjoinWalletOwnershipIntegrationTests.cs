using BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services.Wallets;
using BTCPayServer.Tests;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using Xunit;
using Npgsql;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Services.Stores;
using BTCPayServer.Plugins.Payjoin.Models;

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
        var session = await CreateSessionAsync(tester, context.Merchant, cts.Token).ConfigureAwait(true);
        var result = await ownershipService.CreateResolverAsync(session,
            new PayjoinOriginalTransactionFacts(uint256.One, [coin.OutPoint, otherOutputFromWalletTransaction]),
            cts.Token).ConfigureAwait(true);
        Assert.Equal(PayjoinOwnershipLookupStatus.Ready, result.Status);
        var resolver = Assert.IsType<PayjoinInputOwnershipResolver>(result.Resolver);

        Assert.True(resolver.IsOwned(coin.OutPoint.Hash.ToString(), coin.OutPoint.N));
        Assert.False(resolver.IsOwned(otherOutputFromWalletTransaction.Hash.ToString(), otherOutputFromWalletTransaction.N));
        var unavailable = await ownershipService.CreateResolverAsync(session,
            new PayjoinOriginalTransactionFacts(uint256.Zero, [foreignOutpoint]), cts.Token).ConfigureAwait(true);
        Assert.Equal(PayjoinOwnershipLookupStatus.Deferred, unavailable.Status);
        Assert.Null(unavailable.Resolver);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task ColdSettlementAccountIsForeignToHotSigningAccount()
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
        var session = await CreateSessionAsync(tester, context.Merchant, cts.Token).ConfigureAwait(true);
        var result = await ownershipService.CreateResolverAsync(session,
            new PayjoinOriginalTransactionFacts(uint256.One, [coldOutpoint!]), cts.Token).ConfigureAwait(true);
        Assert.Equal(PayjoinOwnershipLookupStatus.Ready, result.Status);
        var resolver = Assert.IsType<PayjoinInputOwnershipResolver>(result.Resolver);

        Assert.Equal(PayjoinInputOwnership.Foreign, resolver.GetOwnership(coldOutpoint!));
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task RegisteredHotInputIsRejectedWhenFundingHistoryIsMissing()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(
            tester, initialFundingUtxoCount: 1, cancellationToken: cts.Token).ConfigureAwait(true);
        var client = tester.PayTester.GetService<ExplorerClientProvider>().GetExplorerClient(context.Network);
        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);
        var invoice = await PayjoinInvoiceTestHelper.PreparePayjoinInvoiceAsync(tester, context.Merchant, context.Network, cts.Token).ConfigureAwait(true);
        var historyRemoved = false;
        var payer = new PayjoinTestPayer(tester, context.Merchant, context.Network, useReservedSenderChangeAddress: true,
            beforeOriginalAsync: async (original, token) =>
            {
                using var database = new NpgsqlConnection(tester.PayTester.ExplorerPostgres);
                await database.OpenAsync(token).ConfigureAwait(true);
                foreach (var input in original.GetGlobalTransaction().Inputs)
                {
                    using var command = new NpgsqlCommand("DELETE FROM txs WHERE code = 'BTC' AND tx_id = @txid", database);
                    command.Parameters.AddWithValue("txid", input.PrevOut.Hash.ToString());
                    Assert.Equal(1, await command.ExecuteNonQueryAsync(token).ConfigureAwait(true));
                    Assert.Null(await client.GetTransactionAsync(context.Merchant.DerivationScheme, input.PrevOut.Hash, token).ConfigureAwait(true));
                    var session = await CreateSessionAsync(tester, context.Merchant, token).ConfigureAwait(true);
                    var result = await tester.PayTester.GetService<IPayjoinWalletOwnershipService>().CreateResolverAsync(
                        session, new PayjoinOriginalTransactionFacts(original.GetGlobalTransaction().GetHash(), [input.PrevOut]), token).ConfigureAwait(true);
                    Assert.Equal(PayjoinOwnershipLookupStatus.Ready, result.Status);
                    var resolver = Assert.IsType<PayjoinInputOwnershipResolver>(result.Resolver);
                    Assert.Equal(PayjoinInputOwnership.Owned, resolver.GetOwnership(input.PrevOut));
                }
                historyRemoved = true;
            });
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            payer.PayAsync(invoice.PaymentUrl, invoice.OhttpRelayUrls, cts.Token)).ConfigureAwait(true);
        Assert.True(historyRemoved);
        Assert.Contains("Payjoin receiver rejected the proposal", failure.Message, StringComparison.Ordinal);
        using var db = tester.PayTester.GetService<PayjoinPluginDbContextFactory>().CreateContext();
        Assert.Empty(db.ReceiverInputReservations);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task ResolverRecognizesConfirmedForeignOutpointOutsideWalletHistory()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(
            tester, initialFundingUtxoCount: 1, cancellationToken: cts.Token).ConfigureAwait(true);
        var client = tester.PayTester.GetService<ExplorerClientProvider>().GetExplorerClient(context.Network);
        using var foreignKey = new Key();
        var script = foreignKey.PubKey.WitHash.ScriptPubKey;
        var fundingId = await tester.ExplorerNode.SendToAddressAsync(
            script.GetDestinationAddress(context.Network.NBitcoinNetwork)!, Money.Coins(0.1m), cancellationToken: cts.Token).ConfigureAwait(true);
        var funding = await tester.ExplorerNode.GetRawTransactionAsync(fundingId, cancellationToken: cts.Token).ConfigureAwait(true);
        var input = new OutPoint(fundingId, (uint)Enumerable.Range(0, funding.Outputs.Count).Single(index => funding.Outputs[index].ScriptPubKey == script));
        await tester.ExplorerNode.GenerateAsync(1, cts.Token).ConfigureAwait(true);
        await Tests.TestUtils.EventuallyAsync(async () =>
            Assert.True((await client.GetStatusAsync(cts.Token).ConfigureAwait(true)).IsFullySynched)).ConfigureAwait(true);
        Assert.Null(await client.GetTransactionAsync(context.Merchant.DerivationScheme, fundingId, cts.Token).ConfigureAwait(true));

        var indexes = await tester.ExplorerNode.SendCommandAsync("getindexinfo", cts.Token).ConfigureAwait(true);
        if (indexes.Result["txindex"] is null)
        {
            Assert.Null(await client.GetTransactionAsync(fundingId, cts.Token).ConfigureAwait(true));
        }
        var prevout = await client.RPCClient.GetTxOutAsync(fundingId, (int)input.N, false, cts.Token).ConfigureAwait(true);
        Assert.NotNull(prevout);
        Assert.Equal(script, prevout.TxOut.ScriptPubKey);
        var session = await CreateSessionAsync(tester, context.Merchant, cts.Token).ConfigureAwait(true);
        var result = await tester.PayTester.GetService<IPayjoinWalletOwnershipService>().CreateResolverAsync(
            session, new PayjoinOriginalTransactionFacts(uint256.One, [input]), cts.Token).ConfigureAwait(true);
        Assert.Equal(PayjoinOwnershipLookupStatus.Ready, result.Status);
        var resolver = Assert.IsType<PayjoinInputOwnershipResolver>(result.Resolver);

        Assert.Equal(PayjoinInputOwnership.Foreign, resolver.GetOwnership(input));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Integration", "Integration")]
    public async Task SenderAcceptsAdditionalHotAndColdOutputs(bool disableSubstitution)
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(scope: $"extra-outputs-{disableSubstitution}", newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);
        var sender = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(tester, context.Network, cancellationToken: cts.Token).ConfigureAwait(true);
        var client = tester.PayTester.GetService<ExplorerClientProvider>().GetExplorerClient(context.Network);
        var cold = await PayjoinIntegrationTestSupport.CreateTrackedColdWalletAsync(tester, cts.Token).ConfigureAwait(true);
        var hotAddress = await client.GetUnusedAsync(context.Merchant.DerivationScheme, DerivationFeature.Deposit, 0, true, cts.Token).ConfigureAwait(true);
        var coldAddress = await client.GetUnusedAsync(cold, DerivationFeature.Deposit, 0, true, cts.Token).ConfigureAwait(true);
        Assert.NotNull(hotAddress);
        Assert.NotNull(coldAddress);
        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId,
            settings => settings.ColdWalletDerivationScheme = cold.ToString(), cts.Token).ConfigureAwait(true);
        var invoice = await PayjoinInvoiceTestHelper.PreparePayjoinInvoiceAsync(tester, context.Merchant, context.Network, cts.Token).ConfigureAwait(true);
        var payer = new PayjoinTestPayer(tester, sender, context.Network, additionalDestinations:
        [
            new CreatePSBTDestination { Destination = hotAddress.ScriptPubKey.GetDestinationAddress(context.Network.NBitcoinNetwork), Amount = Money.Coins(0.01m) },
            new CreatePSBTDestination { Destination = coldAddress.ScriptPubKey.GetDestinationAddress(context.Network.NBitcoinNetwork), Amount = Money.Coins(0.02m) }
        ], disableOutputSubstitution: disableSubstitution);
        var payment = await payer.PayAsync(invoice.PaymentUrl, invoice.OhttpRelayUrls, cts.Token).ConfigureAwait(true);
        foreach (var script in new[] { hotAddress.ScriptPubKey, coldAddress.ScriptPubKey })
        {
            var original = Assert.Single(payment.SenderTransaction.Outputs, output => output.ScriptPubKey == script);
            var preserved = Assert.Single(payment.ProposalTransaction.Outputs, output => output.ScriptPubKey == script);
            Assert.True(preserved.Value > Money.Zero && preserved.Value <= original.Value);
            var index = payment.ProposalTransaction.Outputs.IndexOf(preserved);
            var accepted = await client.RPCClient.GetTxOutAsync(uint256.Parse(payment.TransactionId), index, true, cts.Token).ConfigureAwait(true);
            Assert.NotNull(accepted);
            Assert.True(accepted.Confirmations > 0);
        }
        Assert.Equal(disableSubstitution, payment.ProposalTransaction.Outputs.Any(output => output.ScriptPubKey == invoice.InvoiceScript));
        if (!disableSubstitution)
        {
            var settlement = Assert.Single(payment.ProposalTransaction.Outputs,
                output => output.ScriptPubKey != hotAddress.ScriptPubKey && output.ScriptPubKey != coldAddress.ScriptPubKey &&
                    !payment.SenderTransaction.Outputs.Any(original => original.ScriptPubKey == output.ScriptPubKey));
            Assert.NotNull(await client.GetKeyInformationAsync(cold, settlement.ScriptPubKey, cts.Token).ConfigureAwait(true));
        }
    }

    [Theory]
    [InlineData(false, "NBXplorerGenerated")]
    [InlineData(true, "ManualDerivationScheme")]
    [Trait("Integration", "Integration")]
    public async Task UnsupportedProfileKeepsOrdinaryPaymentAndSavedCoinAccess(bool hot, string source)
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(scope: $"unsupported-profile-{hot}-{source}", newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);
        var ownershipSession = await CreateSessionAsync(tester, context.Merchant, cts.Token).ConfigureAwait(true);
        var stores = tester.PayTester.GetService<StoreRepository>();
        var store = await stores.FindStore(context.Merchant.StoreId).ConfigureAwait(true);
        Assert.NotNull(store);
        var handlers = tester.PayTester.GetService<BTCPayServer.Services.Invoices.PaymentMethodHandlerDictionary>();
        var method = PaymentTypes.CHAIN.GetPaymentMethodId("BTC");
        var settings = store.GetPaymentMethodConfig<DerivationSchemeSettings>(method, handlers, true);
        Assert.NotNull(settings);
        settings.IsHotWallet = hot;
        settings.Source = source;
        store.SetPaymentMethodConfig(handlers[method], settings);
        await stores.UpdateStore(store).ConfigureAwait(true);
        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, store.Id, cancellationToken: cts.Token).ConfigureAwait(true);
        var (_, response) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.Merchant, cts.Token).ConfigureAwait(true);
        PayjoinIntegrationTestSupport.AssertPlainBip21(response);
        Assert.Equal(PayjoinAvailabilityStatus.MerchantRequirementsUnmet, response.Status);
        Assert.Equal(PayjoinUnavailableReasons.UnsupportedWalletProfile, response.UnavailableReason);
        var availability = tester.PayTester.GetService<PayjoinAvailabilityService>();
        var coins = await availability.GetConfirmedReceiverCoinsAsync(store.Id, "BTC", context.Network, cts.Token).ConfigureAwait(true);
        Assert.NotEmpty(coins);
        var ownership = await tester.PayTester.GetService<IPayjoinWalletOwnershipService>().CreateResolverAsync(
            ownershipSession, new PayjoinOriginalTransactionFacts(uint256.One, [coins[0].OutPoint]), cts.Token).ConfigureAwait(true);
        Assert.Equal(PayjoinOwnershipLookupStatus.Deferred, ownership.Status);
        Assert.Null(ownership.Resolver);
        var plain = new NBitcoin.Payment.BitcoinUrlBuilder(response.Bip21, context.Network.NBitcoinNetwork);
        var paymentId = await tester.ExplorerNode.SendToAddressAsync(plain.Address!, plain.Amount!, cancellationToken: cts.Token).ConfigureAwait(true);
        var payment = await tester.ExplorerNode.GetRawTransactionAsync(paymentId, cancellationToken: cts.Token).ConfigureAwait(true);
        var index = Enumerable.Range(0, payment.Outputs.Count).Single(i => payment.Outputs[i].ScriptPubKey == plain.Address!.ScriptPubKey);
        await tester.ExplorerNode.GenerateAsync(1, cts.Token).ConfigureAwait(true);
        var accepted = await tester.ExplorerNode.GetTxOutAsync(paymentId, index, true, cts.Token).ConfigureAwait(true);
        Assert.NotNull(accepted);
        Assert.True(accepted.Confirmations > 0);
    }

    private static async Task<PayjoinReceiverSessionState> CreateSessionAsync(ServerTester tester, TestAccount merchant,
        CancellationToken cancellationToken)
    {
        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, merchant.StoreId, cancellationToken: cancellationToken).ConfigureAwait(true);
        var (invoiceId, bip21) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, merchant, cancellationToken).ConfigureAwait(true);
        PayjoinIntegrationTestSupport.AssertPayjoinBip21(bip21);
        return PayjoinReceiverTestHelper.GetRequiredReceiverSession(tester, invoiceId);
    }
}
