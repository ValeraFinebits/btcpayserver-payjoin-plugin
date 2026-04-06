using BTCPayServer.Services.Wallets;
using BTCPayServer.Tests;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;

internal static class PayjoinAccountTestHelper
{
    private const string BitcoinCode = "BTC";
    private static readonly Money InitialWalletFunding = Money.Coins(1.0m);
    private const int InitialFundingUtxoCount = 2;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan WalletFundingConfirmationTimeout = TimeSpan.FromSeconds(30);

    public static async Task<TestContext> CreateInitializedTestContextAsync(ServerTester tester, bool confirmFunding = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tester);

        await tester.StartAsync().WaitAsync(cancellationToken).ConfigureAwait(true);

        var network = GetBitcoinNetwork(tester);
        var user = await CreateInitializedAccountAsync(tester, network, confirmFunding, cancellationToken).ConfigureAwait(true);

        return new TestContext(network, user);
    }

    public static async Task<TestAccount> CreateInitializedAccountAsync(ServerTester tester, BTCPayNetwork network, bool confirmFunding = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tester);
        ArgumentNullException.ThrowIfNull(network);

        var user = tester.NewAccount();
        await user.GrantAccessAsync().WaitAsync(cancellationToken).ConfigureAwait(true);
        await user.RegisterDerivationSchemeAsync(BitcoinCode, ScriptPubKeyType.Segwit, true).WaitAsync(cancellationToken).ConfigureAwait(true);
        await FundWalletAsync(user, network, cancellationToken).ConfigureAwait(true);
        if (confirmFunding)
        {
            await ConfirmWalletFundingAsync(tester, user, network, cancellationToken).ConfigureAwait(true);
        }

        return user;
    }

    private static BTCPayNetwork GetBitcoinNetwork(ServerTester tester)
    {
        var network = tester.NetworkProvider.GetNetwork<BTCPayNetwork>(BitcoinCode);
        Assert.NotNull(network);
        return network;
    }

    private static async Task FundWalletAsync(TestAccount user, BTCPayNetwork network, CancellationToken cancellationToken)
    {
        await user.ReceiveUTXO(InitialWalletFunding, network).WaitAsync(cancellationToken).ConfigureAwait(true);
        await user.ReceiveUTXO(InitialWalletFunding, network).WaitAsync(cancellationToken).ConfigureAwait(true);
    }

    private static async Task ConfirmWalletFundingAsync(ServerTester tester, TestAccount user, BTCPayNetwork network, CancellationToken cancellationToken)
    {
        var rewardAddress = await tester.ExplorerNode.GetNewAddressAsync(cancellationToken).ConfigureAwait(true);
        await tester.ExplorerNode.GenerateToAddressAsync(1, rewardAddress, cancellationToken).ConfigureAwait(true);

        var wallet = tester.PayTester.GetService<BTCPayWalletProvider>().GetWallet(network);
        Assert.NotNull(wallet);

        for (var attempt = 0; attempt < GetAttemptCount(WalletFundingConfirmationTimeout); attempt++)
        {
            var confirmedCoins = await wallet.GetUnspentCoins(user.DerivationScheme, true, cancellationToken).ConfigureAwait(true);
            if (confirmedCoins.Length >= InitialFundingUtxoCount)
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

    internal sealed record TestContext(BTCPayNetwork Network, TestAccount User);
}
