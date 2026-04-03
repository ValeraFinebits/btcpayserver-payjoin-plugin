using BTCPayServer.Data;
using BTCPayServer.Client.Models;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Payjoin.Controllers;
using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Wallets;
using BTCPayServer.Tests;
using Microsoft.AspNetCore.Mvc;
using NBitcoin;
using NBitpayClient;
using NBXplorer.DerivationStrategy;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;

internal static class PayjoinIntegrationTestSupport
{
    private const string BitcoinCode = "BTC";
    private const decimal InvoicePrice = 0.1m;
    private static readonly Money InitialWalletFunding = Money.Coins(1.0m);
    private const int InitialFundingUtxoCount = 2;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan WalletFundingConfirmationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReceiverSessionCreationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReceiverSessionRemovalTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InvoiceStatusTimeout = TimeSpan.FromSeconds(30);

    public static TimeSpan TestTimeout { get; } = TimeSpan.FromMinutes(3);

    public static void AssertSuccessfulPayjoinTransaction((Transaction PayjoinTransaction, Script InvoiceScript, string TransactionId) paymentResult)
    {
        Assert.Equal(uint256.Parse(paymentResult.TransactionId), paymentResult.PayjoinTransaction.GetHash());
        Assert.True(paymentResult.PayjoinTransaction.Inputs.Count > 1,
            $"Expected payjoin tx to contain multiple inputs. Inputs: {paymentResult.PayjoinTransaction.Inputs.Count}");

        Assert.Single(paymentResult.PayjoinTransaction.Outputs, output => output.ScriptPubKey == paymentResult.InvoiceScript);
    }

    public static async Task<TestContext> CreateInitializedTestContextAsync(ServerTester tester, bool confirmFunding = true, CancellationToken cancellationToken = default)
    {
        await tester.StartAsync().WaitAsync(cancellationToken).ConfigureAwait(true);

        var network = GetBitcoinNetwork(tester);
        var user = await CreateInitializedAccountAsync(tester, network, confirmFunding, cancellationToken).ConfigureAwait(true);

        return new TestContext(network, user);
    }

    public static async Task<TestAccount> CreateInitializedAccountAsync(ServerTester tester, BTCPayNetwork network, bool confirmFunding = true, CancellationToken cancellationToken = default)
    {
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

    public static async Task<(string InvoiceId, GetBip21Response Bip21Response)> CreateInvoiceAndGetBip21Async(
        ServerTester tester,
        TestAccount merchant,
        CancellationToken cancellationToken)
    {
        var invoice = await merchant.BitPay.CreateInvoiceAsync(new Invoice
        {
            Price = InvoicePrice,
            Currency = BitcoinCode,
            FullNotifications = true
        }).WaitAsync(cancellationToken).ConfigureAwait(true);

        return (invoice.Id, await GetBip21Async(tester, invoice.Id, cancellationToken).ConfigureAwait(true));
    }

    public static async Task<GetBip21Response> GetBip21Async(ServerTester tester, string invoiceId, CancellationToken cancellationToken)
    {
        var paymentUrlService = tester.PayTester.GetService<PayjoinInvoicePaymentUrlService>();
        var bip21Response = await paymentUrlService.GetInvoicePaymentUrlAsync(invoiceId, cancellationToken).ConfigureAwait(true);
        Assert.NotNull(bip21Response);
        return bip21Response!;
    }

    public static void AssertPlainBip21(GetBip21Response bip21Response)
    {
        Assert.False(bip21Response.PayjoinEnabled);
        Assert.DoesNotContain("pj=", bip21Response.Bip21, StringComparison.OrdinalIgnoreCase);
    }

    public static void AssertPayjoinBip21(GetBip21Response bip21Response)
    {
        Assert.True(bip21Response.PayjoinEnabled);
        Assert.Contains("pj=", bip21Response.Bip21, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task EnablePayjoinAsync(ServerTester tester, string storeId, Action<PayjoinStoreSettings>? configure = null, CancellationToken cancellationToken = default)
    {
        var settings = await tester.PayTester.GetService<IPayjoinStoreSettingsRepository>().GetAsync(storeId).WaitAsync(cancellationToken).ConfigureAwait(true);
        settings.EnabledByDefault = true;
        configure?.Invoke(settings);
        await UpdateStoreSettingsAsync(tester, storeId, settings, cancellationToken).ConfigureAwait(true);
    }

    public static async Task DisablePayjoinAsync(ServerTester tester, string storeId, Action<PayjoinStoreSettings>? configure = null, CancellationToken cancellationToken = default)
    {
        var settings = await tester.PayTester.GetService<IPayjoinStoreSettingsRepository>().GetAsync(storeId).WaitAsync(cancellationToken).ConfigureAwait(true);
        settings.EnabledByDefault = false;
        configure?.Invoke(settings);
        await UpdateStoreSettingsAsync(tester, storeId, settings, cancellationToken).ConfigureAwait(true);
    }

    public static async Task UpdateStoreSettingsAsync(ServerTester tester, string storeId, PayjoinStoreSettings settings, CancellationToken cancellationToken = default)
    {
        var storeSettingsRepository = tester.PayTester.GetService<IPayjoinStoreSettingsRepository>();
        await storeSettingsRepository.SetAsync(storeId, settings).WaitAsync(cancellationToken).ConfigureAwait(true);
    }

    public static async Task<DerivationStrategyBase> CreateTrackedColdWalletAsync(ServerTester tester, CancellationToken cancellationToken = default)
    {
        var coldKey = new ExtKey();
        var coldAccountKey = coldKey.Derive(new KeyPath("m/84'/1'/0'"));
        var factory = tester.ExplorerClient.Network.DerivationStrategyFactory;
        var coldDerivation = factory.CreateDirectDerivationStrategy(
            coldAccountKey.Neuter(),
            new DerivationStrategyOptions { ScriptPubKeyType = ScriptPubKeyType.Segwit });

        await tester.ExplorerClient.TrackAsync(coldDerivation, cancellationToken).ConfigureAwait(true);

        var coldUtxosBefore = await tester.ExplorerClient.GetUTXOsAsync(coldDerivation, cancellationToken).ConfigureAwait(true);
        Assert.Empty(coldUtxosBefore.GetUnspentUTXOs());

        return coldDerivation;
    }

    public static async Task AssertColdWalletReceivedPayjoinChangeAsync(
        ServerTester tester,
        DerivationStrategyBase coldDerivation,
        (Transaction PayjoinTransaction, Script InvoiceScript, string TransactionId) paymentResult,
        CancellationToken cancellationToken = default)
    {
        var explorerClient = tester.PayTester.GetService<ExplorerClientProvider>().GetExplorerClient(GetBitcoinNetwork(tester));
        var coldWalletUtxos = await explorerClient.GetUTXOsAsync(coldDerivation, cancellationToken).ConfigureAwait(true);
        Assert.NotNull(coldWalletUtxos);

        var coldUtxo = Assert.Single(coldWalletUtxos.GetUnspentUTXOs());
        Assert.NotEqual(paymentResult.InvoiceScript, coldUtxo.ScriptPubKey);
        Assert.True((Money)coldUtxo.Value > Money.Zero, "Cold wallet UTXO should have positive value");
        var coldTxOutput = paymentResult.PayjoinTransaction.Outputs.FirstOrDefault(o => o.ScriptPubKey == coldUtxo.ScriptPubKey);
        Assert.NotNull(coldTxOutput);
        Assert.Equal((Money)coldUtxo.Value, coldTxOutput.Value);
    }

    public static async Task<(Transaction PayjoinTransaction, Script InvoiceScript, string TransactionId)> CreateAndPayInvoiceViaRunTestPaymentAsync(
        ServerTester tester,
        TestAccount user,
        BTCPayNetwork network,
        CancellationToken cancellationToken)
    {
        var payjoinContext = await PreparePayjoinInvoiceAsync(tester, user, network, cancellationToken).ConfigureAwait(true);

        var controller = tester.PayTester.GetController<UIPayJoinController>();
        var paymentActionResult = await controller.RunTestPayment(new RunTestPaymentRequest
        {
            InvoiceId = payjoinContext.InvoiceId,
            PaymentUrl = payjoinContext.PaymentUrl
        }, cancellationToken).ConfigureAwait(true);
        var paymentResult = Assert.IsType<OkObjectResult>(paymentActionResult.Result);
        var paymentResponse = Assert.IsType<RunTestPaymentResponse>(paymentResult.Value);
        Assert.True(paymentResponse.Succeeded, paymentResponse.Message);
        Assert.False(string.IsNullOrWhiteSpace(paymentResponse.TransactionId), "TransactionId must be returned on success");

        return await FinalizePayjoinPaymentAsync(tester, user, payjoinContext, paymentResponse.TransactionId!, cancellationToken).ConfigureAwait(true);
    }

    public static async Task<(Transaction PayjoinTransaction, Script InvoiceScript, string TransactionId)> CreateAndPayInvoiceViaExternalPayjoinPayerAsync(
        ServerTester tester,
        TestAccount merchant,
        TestAccount payer,
        BTCPayNetwork network,
        CancellationToken cancellationToken)
    {
        return await CreateAndPayInvoiceViaExternalPayjoinPayerAsync(tester, merchant, payer, network, preProposalPollDelay: null, cancellationToken).ConfigureAwait(true);
    }

    public static async Task<(Transaction PayjoinTransaction, Script InvoiceScript, string TransactionId)> CreateAndPayInvoiceViaExternalPayjoinPayerAsync(
        ServerTester tester,
        TestAccount merchant,
        TestAccount payer,
        BTCPayNetwork network,
        TimeSpan? preProposalPollDelay,
        CancellationToken cancellationToken)
    {
        var payjoinContext = await PreparePayjoinInvoiceAsync(tester, merchant, network, cancellationToken).ConfigureAwait(true);
        var payjoinPayer = new PayjoinTestPayer(tester, payer, network);
        var paymentResult = await payjoinPayer.PayAsync(payjoinContext.PaymentUrl, payjoinContext.OhttpRelayUrl, preProposalPollDelay, cancellationToken).ConfigureAwait(true);

        return await FinalizePayjoinPaymentAsync(tester, merchant, payjoinContext, paymentResult.TransactionId, cancellationToken).ConfigureAwait(true);
    }

    public static async Task<string> PayInvoiceViaExternalPayjoinPayerAsync(
        ServerTester tester,
        TestAccount payer,
        BTCPayNetwork network,
        string storeId,
        Uri paymentUrl,
        CancellationToken cancellationToken)
    {
        return await PayInvoiceViaExternalPayjoinPayerAsync(tester, payer, network, storeId, paymentUrl, preProposalPollDelay: null, cancellationToken).ConfigureAwait(true);
    }

    public static async Task<string> PayInvoiceViaExternalPayjoinPayerAsync(
        ServerTester tester,
        TestAccount payer,
        BTCPayNetwork network,
        string storeId,
        Uri paymentUrl,
        TimeSpan? preProposalPollDelay,
        CancellationToken cancellationToken)
    {
        var storeSettings = await tester.PayTester.GetService<IPayjoinStoreSettingsRepository>().GetAsync(storeId).WaitAsync(cancellationToken).ConfigureAwait(true);
        Assert.NotNull(storeSettings.OhttpRelayUrl);

        var payjoinPayer = new PayjoinTestPayer(tester, payer, network);
        var paymentResult = await payjoinPayer.PayAsync(paymentUrl, storeSettings.OhttpRelayUrl, preProposalPollDelay, cancellationToken).ConfigureAwait(true);
        Assert.False(string.IsNullOrWhiteSpace(paymentResult.TransactionId), "TransactionId must be returned on success");

        return paymentResult.TransactionId;
    }

    private static void AssertHasReceiverContribution(Transaction payjoinTx, HashSet<string> receiverOutpointsBeforePayment)
    {
        var receiverContributionFound = payjoinTx.Inputs.Any(input => receiverOutpointsBeforePayment.Contains(input.PrevOut.ToString()));
        Assert.True(receiverContributionFound,
            $"Expected payjoin tx to spend at least one receiver-owned coin. Inputs: {string.Join(", ", payjoinTx.Inputs.Select(input => input.PrevOut))}");
    }

    private static BTCPayNetwork GetBitcoinNetwork(ServerTester tester)
    {
        var network = tester.NetworkProvider.GetNetwork<BTCPayNetwork>(BitcoinCode);
        Assert.NotNull(network);
        return network;
    }

    private static async Task FundWalletAsync(TestAccount user, BTCPayNetwork network, CancellationToken cancellationToken = default)
    {
        await user.ReceiveUTXO(InitialWalletFunding, network).WaitAsync(cancellationToken).ConfigureAwait(true);
        await user.ReceiveUTXO(InitialWalletFunding, network).WaitAsync(cancellationToken).ConfigureAwait(true);
    }

    private static async Task ConfirmWalletFundingAsync(ServerTester tester, TestAccount user, BTCPayNetwork network, CancellationToken cancellationToken = default)
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

    private static PaymentMethodId GetBitcoinPaymentMethodId()
    {
        return PaymentTypes.CHAIN.GetPaymentMethodId(BitcoinCode);
    }

    private static async Task<PayjoinInvoiceContext> PreparePayjoinInvoiceAsync(
        ServerTester tester,
        TestAccount merchant,
        BTCPayNetwork network,
        CancellationToken cancellationToken)
    {
        var invoice = await merchant.BitPay.CreateInvoiceAsync(new Invoice
        {
            Price = InvoicePrice,
            Currency = BitcoinCode,
            FullNotifications = true
        }).WaitAsync(cancellationToken).ConfigureAwait(true);

        var paymentMethodId = GetBitcoinPaymentMethodId();
        var invoiceBeforePayment = await tester.PayTester.GetService<InvoiceRepository>().GetInvoice(invoice.Id).WaitAsync(cancellationToken).ConfigureAwait(true);
        Assert.NotNull(invoiceBeforePayment);

        var promptBeforePayment = invoiceBeforePayment.GetPaymentPrompt(paymentMethodId);
        Assert.NotNull(promptBeforePayment);

        var expectedDue = promptBeforePayment.Calculate().Due;
        var receiverOutpointsBeforePayment = await GetReceiverOutpointsAsync(tester, merchant.StoreId, confirmedOnly: true, cancellationToken).ConfigureAwait(true);

        var paymentUrlService = tester.PayTester.GetService<PayjoinInvoicePaymentUrlService>();
        var bip21Response = await paymentUrlService.GetInvoicePaymentUrlAsync(invoice.Id, cancellationToken).ConfigureAwait(true);
        Assert.NotNull(bip21Response);
        AssertPayjoinBip21(bip21Response);

        var paymentUrl = new System.Uri(bip21Response.Bip21, System.UriKind.Absolute);
        var storeSettings = await tester.PayTester.GetService<IPayjoinStoreSettingsRepository>().GetAsync(merchant.StoreId).WaitAsync(cancellationToken).ConfigureAwait(true);
        Assert.NotNull(storeSettings.OhttpRelayUrl);

        var invoiceScript = BitcoinAddress.Create(promptBeforePayment.Destination, network.NBitcoinNetwork).ScriptPubKey;
        return new PayjoinInvoiceContext(invoice.Id, paymentMethodId, expectedDue, receiverOutpointsBeforePayment, paymentUrl, storeSettings.OhttpRelayUrl, invoiceScript);
    }

    private static async Task<(Transaction PayjoinTransaction, Script InvoiceScript, string TransactionId)> FinalizePayjoinPaymentAsync(
        ServerTester tester,
        TestAccount merchant,
        PayjoinInvoiceContext payjoinContext,
        string transactionId,
        CancellationToken cancellationToken)
    {
        var payjoinTx = await GetPayjoinTransactionAsync(tester, transactionId, cancellationToken).ConfigureAwait(true);
        AssertHasReceiverContribution(payjoinTx, payjoinContext.ReceiverOutpointsBeforePayment);

        var invoiceOutput = payjoinTx.Outputs.FirstOrDefault(o => o.ScriptPubKey == payjoinContext.InvoiceScript);
        Assert.NotNull(invoiceOutput);
        Assert.Equal(Money.Coins(payjoinContext.ExpectedDue), invoiceOutput.Value);

        await merchant.WaitInvoicePaid(payjoinContext.InvoiceId).WaitAsync(cancellationToken).ConfigureAwait(true);

        var invoiceEntity = await tester.PayTester.GetService<InvoiceRepository>().GetInvoice(payjoinContext.InvoiceId).WaitAsync(cancellationToken).ConfigureAwait(true);
        Assert.NotNull(invoiceEntity);

        var payments = invoiceEntity
            .GetPayments(false)
            .Where(p => p.Accounted && p.PaymentMethodId == payjoinContext.PaymentMethodId)
            .ToList();
        Assert.Single(payments);

        var totalPaid = payments.Sum(p => p.Value);
        Assert.Equal(payjoinContext.ExpectedDue, totalPaid);

        return (payjoinTx, payjoinContext.InvoiceScript, transactionId);
    }

    public static async Task<HashSet<string>> GetReceiverOutpointsAsync(ServerTester tester, string storeId, bool confirmedOnly, CancellationToken cancellationToken)
    {
        var network = GetBitcoinNetwork(tester);

        var store = await tester.PayTester.GetService<StoreRepository>().FindStore(storeId).WaitAsync(cancellationToken).ConfigureAwait(true);
        Assert.NotNull(store);

        var paymentMethodId = GetBitcoinPaymentMethodId();
        var handlers = tester.PayTester.GetService<PaymentMethodHandlerDictionary>();
        var derivationScheme = store.GetPaymentMethodConfig<DerivationSchemeSettings>(paymentMethodId, handlers, true);
        Assert.NotNull(derivationScheme);

        var wallet = tester.PayTester.GetService<BTCPayWalletProvider>().GetWallet(network);
        Assert.NotNull(wallet);

        var receiverCoins = await wallet.GetUnspentCoins(derivationScheme.AccountDerivation, confirmedOnly, cancellationToken).ConfigureAwait(true);

        return receiverCoins
            .Select(coin => coin.OutPoint.ToString())
            .ToHashSet(StringComparer.Ordinal);
    }

    public static Task AssertReceiverSessionEventuallyCreatedAsync(ServerTester tester, string invoiceId, CancellationToken cancellationToken)
    {
        return AssertReceiverSessionStateAsync(tester, invoiceId, shouldExist: true, cancellationToken);
    }

    public static Task AssertReceiverSessionEventuallyRemovedAsync(ServerTester tester, string invoiceId, CancellationToken cancellationToken)
    {
        return AssertReceiverSessionStateAsync(tester, invoiceId, shouldExist: false, cancellationToken);
    }

    public static async Task AssertReceiverSessionEventuallyCloseRequestedOrRemovedAsync(ServerTester tester, string invoiceId, CancellationToken cancellationToken)
    {
        var sessionStore = tester.PayTester.GetService<PayjoinReceiverSessionStore>();
        var maxAttempts = GetAttemptCount(ReceiverSessionRemovalTimeout);

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!sessionStore.TryGetSession(invoiceId, out var session) || session?.IsCloseRequested == true)
            {
                return;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(true);
        }

        Assert.Fail($"Expected receiver session for invoice '{invoiceId}' to be marked for closure or removed.");
    }

    public static async Task AssertInvoiceStatusEventuallyAsync(ServerTester tester, string invoiceId, InvoiceStatus expectedStatus, CancellationToken cancellationToken)
    {
        var invoiceRepository = tester.PayTester.GetService<InvoiceRepository>();

        for (var attempt = 0; attempt < GetAttemptCount(InvoiceStatusTimeout); attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var invoice = await invoiceRepository.GetInvoice(invoiceId).WaitAsync(cancellationToken).ConfigureAwait(true);
            if (invoice is not null && invoice.GetInvoiceState().Status == expectedStatus)
            {
                return;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(true);
        }

        Assert.Fail($"Expected invoice '{invoiceId}' status to become '{expectedStatus}'.");
    }

    public static PayjoinReceiverSessionState GetRequiredReceiverSession(ServerTester tester, string invoiceId)
    {
        var sessionStore = tester.PayTester.GetService<PayjoinReceiverSessionStore>();
        Assert.True(sessionStore.TryGetSession(invoiceId, out var session));
        Assert.NotNull(session);
        return session;
    }

    private static async Task AssertReceiverSessionStateAsync(ServerTester tester, string invoiceId, bool shouldExist, CancellationToken cancellationToken)
    {
        var sessionStore = tester.PayTester.GetService<PayjoinReceiverSessionStore>();
        var timeout = shouldExist ? ReceiverSessionCreationTimeout : ReceiverSessionRemovalTimeout;
        var maxAttempts = GetAttemptCount(timeout);

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var exists = sessionStore.TryGetSession(invoiceId, out _);
            if (exists == shouldExist)
            {
                return;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(true);
        }

        Assert.Fail(shouldExist
            ? $"Expected receiver session for invoice '{invoiceId}' to be created."
            : $"Expected receiver session for invoice '{invoiceId}' to be removed.");
    }

    private static async Task<Transaction> GetPayjoinTransactionAsync(ServerTester tester, string transactionId, CancellationToken cancellationToken)
    {
        var bestBlock = await tester.ExplorerNode.GetBestBlockHashAsync(cancellationToken).ConfigureAwait(true);
        return await tester.ExplorerNode.GetRawTransactionAsync(uint256.Parse(transactionId), bestBlock, cancellationToken: cancellationToken).ConfigureAwait(true);
    }

    private static int GetAttemptCount(TimeSpan timeout)
    {
        var attempts = (int)Math.Ceiling(timeout.TotalMilliseconds / PollInterval.TotalMilliseconds);
        return Math.Max(attempts, 1);
    }

    internal sealed record TestContext(BTCPayNetwork Network, TestAccount User);

    private sealed record PayjoinInvoiceContext(
        string InvoiceId,
        PaymentMethodId PaymentMethodId,
        decimal ExpectedDue,
        HashSet<string> ReceiverOutpointsBeforePayment,
        Uri PaymentUrl,
        Uri OhttpRelayUrl,
        Script InvoiceScript);
}
