using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Wallets;
using BTCPayServer.Tests;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;

internal static class PayjoinAccountTestHelper
{
    private static readonly Money InitialWalletFunding = Money.Coins(1.0m);
    private const int DefaultInitialFundingUtxoCount = 2;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan WalletFundingConfirmationTimeout = TimeSpan.FromSeconds(30);

    public static async Task<TestContext> CreateInitializedTestContextAsync(ServerTester tester, bool confirmFunding = true, int initialFundingUtxoCount = DefaultInitialFundingUtxoCount, ScriptPubKeyType scriptPubKeyType = ScriptPubKeyType.Segwit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tester);

        await tester.StartAsync().WaitAsync(cancellationToken).ConfigureAwait(true);

        var network = GetBitcoinNetwork(tester);
        var merchant = await CreateInitializedAccountAsync(tester, network, confirmFunding, initialFundingUtxoCount, scriptPubKeyType, cancellationToken: cancellationToken).ConfigureAwait(true);

        return new TestContext(network, merchant);
    }

    /// <param name="serverHoldsKeys">
    /// False registers the wallet without giving NBXplorer the private keys, which is what a cold
    /// wallet, a hardware device or a multisig group looks like to BTCPay. The test still holds the
    /// seed through the account's own response, so it can sign the way an operator would.
    /// </param>
    public static async Task<TestAccount> CreateInitializedAccountAsync(ServerTester tester, BTCPayNetwork network, bool confirmFunding = true, int initialFundingUtxoCount = DefaultInitialFundingUtxoCount, ScriptPubKeyType scriptPubKeyType = ScriptPubKeyType.Segwit, bool serverHoldsKeys = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tester);
        ArgumentNullException.ThrowIfNull(network);

        var user = tester.NewAccount();
        await user.GrantAccessAsync().WaitAsync(cancellationToken).ConfigureAwait(true);
        await user.RegisterDerivationSchemeAsync(PayjoinConstants.BitcoinCode, scriptPubKeyType, serverHoldsKeys).WaitAsync(cancellationToken).ConfigureAwait(true);
        await FundWalletAsync(user, network, initialFundingUtxoCount, cancellationToken).ConfigureAwait(true);
        if (confirmFunding)
        {
            await ConfirmWalletFundingAsync(tester, user, network, initialFundingUtxoCount, cancellationToken).ConfigureAwait(true);
        }

        return user;
    }

    public static async Task<TestContext> CreateInitializedP2wshTestContextAsync(ServerTester tester, CancellationToken cancellationToken)
    {
        var context = await CreateInitializedTestContextAsync(tester,
            confirmFunding: false, initialFundingUtxoCount: 0, cancellationToken: cancellationToken).ConfigureAwait(true);
        var factory = tester.ExplorerClient.Network.DerivationStrategyFactory;
        var derivation = (StandardDerivationStrategyBase)factory.CreateMultiSigDerivationStrategy(
            new[] { new ExtKey().Neuter() }, 1,
            new DerivationStrategyOptions { ScriptPubKeyType = ScriptPubKeyType.Segwit });
        var wallet = tester.PayTester.GetService<BTCPayWalletProvider>().GetWallet(context.Network);
        Assert.NotNull(wallet);
        await wallet.TrackAsync(derivation).WaitAsync(cancellationToken).ConfigureAwait(true);

        var storeRepository = tester.PayTester.GetService<StoreRepository>();
        var store = await storeRepository.FindStore(context.Merchant.StoreId).WaitAsync(cancellationToken).ConfigureAwait(true);
        Assert.NotNull(store);
        var handlers = tester.PayTester.GetService<PaymentMethodHandlerDictionary>();
        var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(PayjoinConstants.BitcoinCode);
        store.SetPaymentMethodConfig(handlers[paymentMethodId], new DerivationSchemeSettings(derivation, context.Network));
        await storeRepository.UpdateStore(store).WaitAsync(cancellationToken).ConfigureAwait(true);

        context.Merchant.GenerateWalletResponseV.DerivationScheme = derivation;
        await FundWalletAsync(context.Merchant, context.Network, 1, cancellationToken).ConfigureAwait(true);
        await ConfirmWalletFundingAsync(tester, context.Merchant, context.Network, 1, cancellationToken).ConfigureAwait(true);
        return context;
    }

    private static BTCPayNetwork GetBitcoinNetwork(ServerTester tester)
    {
        var network = tester.NetworkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode);
        Assert.NotNull(network);
        return network;
    }

    private static async Task FundWalletAsync(TestAccount user, BTCPayNetwork network, int initialFundingUtxoCount, CancellationToken cancellationToken)
    {
        for (var i = 0; i < initialFundingUtxoCount; i++)
        {
            await user.ReceiveUTXO(InitialWalletFunding, network).WaitAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    private static async Task ConfirmWalletFundingAsync(ServerTester tester, TestAccount user, BTCPayNetwork network, int initialFundingUtxoCount, CancellationToken cancellationToken)
    {
        var rewardAddress = await tester.ExplorerNode.GetNewAddressAsync(cancellationToken).ConfigureAwait(true);
        await tester.ExplorerNode.GenerateToAddressAsync(1, rewardAddress, cancellationToken).ConfigureAwait(true);

        var wallet = tester.PayTester.GetService<BTCPayWalletProvider>().GetWallet(network);
        Assert.NotNull(wallet);

        for (var attempt = 0; attempt < GetAttemptCount(WalletFundingConfirmationTimeout); attempt++)
        {
            var confirmedCoins = await wallet.GetUnspentCoins(user.DerivationScheme, true, cancellationToken).ConfigureAwait(true);
            if (confirmedCoins.Length >= initialFundingUtxoCount)
            {
                return;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(true);
        }

        throw new Xunit.Sdk.XunitException($"Wallet funding for store '{user.StoreId}' was not confirmed in time.");
    }

    private static int GetAttemptCount(TimeSpan timeout)
    {
        var attempts = (int)Math.Ceiling(timeout.TotalMilliseconds / PollInterval.TotalMilliseconds);
        return Math.Max(attempts, 1);
    }

    internal sealed record TestContext(BTCPayNetwork Network, TestAccount Merchant);
}
