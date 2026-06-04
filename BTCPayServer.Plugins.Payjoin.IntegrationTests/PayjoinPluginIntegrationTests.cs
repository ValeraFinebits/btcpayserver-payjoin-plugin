using BTCPayServer.BIP78.Sender;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Payments;
using BTCPayServer.Payments.PayJoin.Sender;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using BTCPayServer.Tests;
using NBitcoin;
using NBitcoin.Payment;
using NBitpayClient;
using NBXplorer.Models;
using Xunit;
using Xunit.Abstractions;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class PayjoinPluginIntegrationTests : UnitTestBase
{
    public PayjoinPluginIntegrationTests(ITestOutputHelper helper) : base(helper)
    {
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task CreateInvoiceAndPayItThroughThePayjoinPluginWithExternalPayer()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);
        var payer = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(tester, context.Network, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var paymentResult = await PayjoinIntegrationTestSupport.CreateAndPayInvoiceViaExternalPayjoinPayerAsync(tester, context.Merchant, payer, context.Network, cts.Token).ConfigureAwait(true);

        PayjoinIntegrationTestSupport.AssertSuccessfulPayjoinTransaction(paymentResult);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task CreateInvoiceAndPayItThroughThePayjoinPluginWithSameWalletPayer()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var paymentResult = await PayjoinIntegrationTestSupport.CreateAndPayInvoiceViaSameWalletPayjoinPayerAsync(tester, context.Merchant, context.Network, cts.Token).ConfigureAwait(true);

        PayjoinIntegrationTestSupport.AssertSuccessfulPayjoinTransaction(paymentResult);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task BuiltInWalletPayingPluginBip21CompletesPayjoinWithoutReceiverScriptSubstitution()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);
        var payer = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(tester, context.Network, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var receiverOutpointsBeforePayment = await PayjoinIntegrationTestSupport.GetReceiverOutpointsAsync(
            tester,
            context.Merchant.StoreId,
            confirmedOnly: true,
            cts.Token).ConfigureAwait(true);
        Assert.NotEmpty(receiverOutpointsBeforePayment);

        var (invoiceId, bip21Response) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.Merchant, cts.Token).ConfigureAwait(true);
        PayjoinIntegrationTestSupport.AssertPayjoinBip21(bip21Response);

        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);

        var bip21 = new BitcoinUrlBuilder(bip21Response.Bip21, context.Network.NBitcoinNetwork);
        Assert.NotNull(bip21.Address);
        Assert.NotNull(bip21.Amount);

        var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(PayjoinConstants.BitcoinCode);

        var invoiceRepository = tester.PayTester.GetService<InvoiceRepository>();
        var invoice = await invoiceRepository.GetInvoice(invoiceId).WaitAsync(cts.Token).ConfigureAwait(true);
        Assert.NotNull(invoice);
        var prompt = invoice!.GetPaymentPrompt(paymentMethodId);
        Assert.NotNull(prompt);
        var payjoinContext = new PayjoinInvoiceTestHelper.PayjoinInvoiceContext(
            invoiceId,
            paymentMethodId,
            prompt!.Calculate().Due,
            receiverOutpointsBeforePayment,
            new Uri(bip21Response.Bip21, UriKind.Absolute),
            new Uri("https://example.invalid", UriKind.Absolute),
            new Uri("https://example.invalid", UriKind.Absolute),
            bip21.Address.ScriptPubKey);

        var explorerClient = tester.PayTester.GetService<ExplorerClientProvider>().GetExplorerClient(context.Network);
        var originalPsbt = (await explorerClient.CreatePSBTAsync(
            payer.DerivationScheme,
            new CreatePSBTRequest
            {
                FeePreference = new FeePreference
                {
                    ExplicitFeeRate = new FeeRate(5m)
                },
                Destinations =
                {
                    new CreatePSBTDestination
                    {
                        Destination = bip21.Address,
                        Amount = bip21.Amount
                    }
                }
            },
            cts.Token).ConfigureAwait(true)).PSBT;
        originalPsbt = await payer.Sign(originalPsbt).ConfigureAwait(true);

        var payerStore = await tester.PayTester.GetService<StoreRepository>().FindStore(payer.StoreId).ConfigureAwait(true);
        Assert.NotNull(payerStore);

        var handlers = tester.PayTester.GetService<PaymentMethodHandlerDictionary>();
        var derivationSchemeSettings = payerStore!.GetPaymentMethodConfig<global::BTCPayServer.DerivationSchemeSettings>(paymentMethodId, handlers);
        Assert.NotNull(derivationSchemeSettings);

        var payjoinClient = tester.PayTester.GetService<PayjoinClient>();
        var payjoinPsbt = await payjoinClient.RequestPayjoin(
            bip21,
            new PayjoinWallet(derivationSchemeSettings),
            originalPsbt,
            cts.Token).ConfigureAwait(true);
        payjoinPsbt = await payer.Sign(payjoinPsbt).ConfigureAwait(true);

        Assert.True(payjoinPsbt.TryFinalize(out _));
        var walletPayment = payjoinPsbt.ExtractTransaction();
        await tester.ExplorerNode.SendRawTransactionAsync(walletPayment, cts.Token).ConfigureAwait(true);
        var rewardAddress = await tester.ExplorerNode.GetNewAddressAsync(cts.Token).ConfigureAwait(true);
        await tester.ExplorerNode.GenerateToAddressAsync(1, rewardAddress, cts.Token).ConfigureAwait(true);

        await context.Merchant.WaitInvoicePaid(invoiceId).WaitAsync(cts.Token).ConfigureAwait(true);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyRemovedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);
        await PayjoinInvoiceTestHelper.FinalizePayjoinPaymentAsync(tester, context.Merchant, payjoinContext, walletPayment.GetHash().ToString(), cts.Token).ConfigureAwait(true);
        await PayjoinInvoiceTestHelper.AssertInvoiceNotOverpaidEventuallyAsync(tester, payjoinContext, cts.Token).ConfigureAwait(true);

        var bestBlock = await tester.ExplorerNode.GetBestBlockHashAsync(cts.Token).ConfigureAwait(true);
        var broadcastedTransaction = await tester.ExplorerNode
            .GetRawTransactionAsync(walletPayment.GetHash(), bestBlock, cancellationToken: cts.Token)
            .ConfigureAwait(true);

        Assert.Contains(
            broadcastedTransaction.Inputs,
            input => receiverOutpointsBeforePayment.Contains(input.PrevOut.ToString()));

        var receiverOutputs = broadcastedTransaction.Outputs
            .Where(output => output.ScriptPubKey == bip21.Address.ScriptPubKey)
            .ToArray();
        Assert.Single(receiverOutputs);
        Assert.True(
            receiverOutputs[0].Value > bip21.Amount,
            $"Expected receiver output value to grow above the exact invoice amount. Receiver output: {receiverOutputs[0].Value}, invoice amount: {bip21.Amount}.");
        Assert.True(
            broadcastedTransaction.Inputs.Count >= 1,
            $"Expected compat payjoin transaction to contain sender inputs. Inputs: {string.Join(", ", broadcastedTransaction.Inputs.Select(input => input.PrevOut))}");
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task ExternalPayerSucceedsWhenReceiverProposalIsReplayedAcrossPollerTicks()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);
        var payer = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(tester, context.Network, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var (invoiceId, bip21Response) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.Merchant, cts.Token).ConfigureAwait(true);
        PayjoinIntegrationTestSupport.AssertPayjoinBip21(bip21Response);

        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);

        var delay = TimeSpan.FromSeconds(5);
        var paymentTask = PayjoinIntegrationTestSupport.PayInvoiceViaExternalPayjoinPayerAsync(
            tester,
            payer,
            context.Network,
            context.Merchant.StoreId,
            new Uri(bip21Response.Bip21, UriKind.Absolute),
            delay,
            cts.Token);

        await Task.Delay(delay, cts.Token).ConfigureAwait(true);

        var session = PayjoinReceiverTestHelper.GetRequiredReceiverSession(tester, invoiceId);
        Assert.True(session.TryGetContributedInput(out _));

        var transactionId = await paymentTask.ConfigureAwait(true);
        Assert.False(string.IsNullOrWhiteSpace(transactionId));

        await context.Merchant.WaitInvoicePaid(invoiceId).WaitAsync(cts.Token).ConfigureAwait(true);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyRemovedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task SuccessfulPayjoinRemovesReceiverSession()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);
        var payer = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(tester, context.Network, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var (invoiceId, bip21Response) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.Merchant, cts.Token).ConfigureAwait(true);
        PayjoinIntegrationTestSupport.AssertPayjoinBip21(bip21Response);

        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.PayInvoiceViaExternalPayjoinPayerAsync(
            tester,
            payer,
            context.Network,
            context.Merchant.StoreId,
            new Uri(bip21Response.Bip21, UriKind.Absolute),
            cts.Token).ConfigureAwait(true);

        await context.Merchant.WaitInvoicePaid(invoiceId).WaitAsync(cts.Token).ConfigureAwait(true);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyRemovedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task InFlightReceiverSessionSurvivesServerRestartAndCompletesPayjoin()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);
        var payer = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(tester, context.Network, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var (invoiceId, bip21Response) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.Merchant, cts.Token).ConfigureAwait(true);
        PayjoinIntegrationTestSupport.AssertPayjoinBip21(bip21Response);

        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);
        var sessionBeforeRestart = PayjoinReceiverTestHelper.GetRequiredReceiverSession(tester, invoiceId);
        Assert.NotEmpty(sessionBeforeRestart.GetEvents());

        await RestartPayServerAsync(tester, cts.Token).ConfigureAwait(true);

        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);
        var sessionAfterRestart = PayjoinReceiverTestHelper.GetRequiredReceiverSession(tester, invoiceId);
        Assert.Equal(sessionBeforeRestart.GetEvents(), sessionAfterRestart.GetEvents());

        await PayjoinIntegrationTestSupport.PayInvoiceViaExternalPayjoinPayerAsync(
            tester,
            payer,
            context.Network,
            context.Merchant.StoreId,
            new Uri(bip21Response.Bip21, UriKind.Absolute),
            cts.Token).ConfigureAwait(true);

        await context.Merchant.WaitInvoicePaid(invoiceId).WaitAsync(cts.Token).ConfigureAwait(true);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyRemovedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task DisablePayjoinPreservesExistingStoreSettings()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        var coldDerivation = await PayjoinIntegrationTestSupport.CreateTrackedColdWalletAsync(tester, cts.Token).ConfigureAwait(true);
        var expectedDirectoryUrl = new Uri("https://directory.example.test/");
        var expectedRelayUrl = new Uri("https://relay.example.test/");

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, settings =>
        {
            settings.ColdWalletDerivationScheme = coldDerivation.ToString();
            settings.DirectoryUrl = expectedDirectoryUrl;
            settings.OhttpRelayUrl = expectedRelayUrl;
        }, cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.DisablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var storeSettings = await tester.PayTester.GetService<IPayjoinStoreSettingsRepository>().GetAsync(context.Merchant.StoreId).WaitAsync(cts.Token).ConfigureAwait(true);
        Assert.False(storeSettings.PayjoinV2Enabled);
        Assert.Equal(coldDerivation.ToString(), storeSettings.ColdWalletDerivationScheme);
        Assert.Equal(expectedDirectoryUrl, storeSettings.DirectoryUrl);
        Assert.Equal(expectedRelayUrl, storeSettings.OhttpRelayUrl);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task GetBip21DoesNotEnablePayjoinWhenMerchantHasNoCoins()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        await tester.StartAsync().WaitAsync(cts.Token).ConfigureAwait(true);

        var network = tester.NetworkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode);
        Assert.NotNull(network);

        var merchant = tester.NewAccount();
        await merchant.GrantAccessAsync().WaitAsync(cts.Token).ConfigureAwait(true);
        await merchant.RegisterDerivationSchemeAsync(PayjoinConstants.BitcoinCode, ScriptPubKeyType.Segwit, true).WaitAsync(cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var confirmedOutpoints = await PayjoinIntegrationTestSupport.GetReceiverOutpointsAsync(tester, merchant.StoreId, confirmedOnly: true, cts.Token).ConfigureAwait(true);
        Assert.Empty(confirmedOutpoints);

        var (_, bip21Response) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, merchant, cts.Token).ConfigureAwait(true);

        PayjoinIntegrationTestSupport.AssertPlainBip21(bip21Response);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task PayjoinChangeOutputGoesToColdWalletWithExternalPayer()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);
        var payer = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(tester, context.Network, cancellationToken: cts.Token).ConfigureAwait(true);

        var coldDerivation = await PayjoinIntegrationTestSupport.CreateTrackedColdWalletAsync(tester, cts.Token).ConfigureAwait(true);
        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, settings =>
        {
            settings.ColdWalletDerivationScheme = coldDerivation.ToString();
        }, cts.Token).ConfigureAwait(true);

        var paymentResult = await PayjoinIntegrationTestSupport.CreateAndPayInvoiceViaExternalPayjoinPayerAsync(tester, context.Merchant, payer, context.Network, cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.AssertColdWalletReceivedPayjoinChangeAsync(tester, coldDerivation, paymentResult, cts.Token).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task PayjoinAccountingDoesNotMarkInvoiceAsOverpaid()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);
        var payer = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(tester, context.Network, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var payjoinContext = await PayjoinInvoiceTestHelper.PreparePayjoinInvoiceAsync(tester, context.Merchant, context.Network, cts.Token).ConfigureAwait(true);
        var payjoinPayer = new PayjoinTestPayer(tester, payer, context.Network);
        var paymentResult = await payjoinPayer.PayAsync(payjoinContext.PaymentUrl, payjoinContext.OhttpRelayUrl, cts.Token).ConfigureAwait(true);

        await PayjoinInvoiceTestHelper.FinalizePayjoinPaymentAsync(tester, context.Merchant, payjoinContext, paymentResult.TransactionId, cts.Token).ConfigureAwait(true);
        await PayjoinInvoiceTestHelper.AssertInvoiceNotOverpaidEventuallyAsync(tester, payjoinContext, cts.Token).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task StalePaidOverCorrectionClearsPersistedStalePaidOverOnSettledInvoice()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        var (invoiceId, paymentMethodId, exactDue) = await CreateSettledOverpaidInvoiceAsync(tester, context.Merchant, context.Network, cts.Token).ConfigureAwait(true);

        var invoiceRepository = tester.PayTester.GetService<InvoiceRepository>();
        var paymentService = tester.PayTester.GetService<PaymentService>();
        var correctionService = tester.PayTester.GetService<IPayjoinStalePaidOverCorrectionService>();

        var invoice = await invoiceRepository.GetInvoice(invoiceId).WaitAsync(cts.Token).ConfigureAwait(true);
        Assert.NotNull(invoice);

        var payment = invoice!.GetPayments(false).Single(p => p.Accounted && p.PaymentMethodId == paymentMethodId);
        payment.Value = exactDue;
        await paymentService.UpdatePayments([payment]).ConfigureAwait(true);

        var staleInvoice = await invoiceRepository.GetInvoice(invoiceId).WaitAsync(cts.Token).ConfigureAwait(true);
        Assert.NotNull(staleInvoice);
        Assert.Equal(InvoiceExceptionStatus.PaidOver, staleInvoice!.GetInvoiceState().ExceptionStatus);
        Assert.False(staleInvoice.IsOverPaid);

        await correctionService.ClearStalePaidOverAsync(invoiceId).ConfigureAwait(true);

        var correctedInvoice = await AssertInvoiceStateEventuallyAsync(
            tester,
            invoiceId,
            InvoiceStatus.Settled,
            InvoiceExceptionStatus.None,
            cts.Token).ConfigureAwait(true);
        Assert.False(correctedInvoice.IsOverPaid);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task StalePaidOverCorrectionDoesNotClearLegitimateSettledOverpay()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        var (invoiceId, _, _) = await CreateSettledOverpaidInvoiceAsync(tester, context.Merchant, context.Network, cts.Token).ConfigureAwait(true);
        var correctionService = tester.PayTester.GetService<IPayjoinStalePaidOverCorrectionService>();

        await correctionService.ClearStalePaidOverAsync(invoiceId).ConfigureAwait(true);

        var invoice = await AssertInvoiceStateEventuallyAsync(
            tester,
            invoiceId,
            InvoiceStatus.Settled,
            InvoiceExceptionStatus.PaidOver,
            cts.Token).ConfigureAwait(true);
        Assert.True(invoice.IsOverPaid);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task PayjoinInvoiceTransitionsFromProcessingToSettledAfterConfirmation()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);
        var payer = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(tester, context.Network, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var payjoinContext = await PayjoinInvoiceTestHelper.PreparePayjoinInvoiceAsync(tester, context.Merchant, context.Network, cts.Token).ConfigureAwait(true);
        var transactionId = await PayjoinIntegrationTestSupport.PayInvoiceViaExternalPayjoinPayerAsync(
            tester,
            payer,
            context.Network,
            context.Merchant.StoreId,
            payjoinContext.PaymentUrl,
            preProposalPollDelay: null,
            mineBlockAfterBroadcast: false,
            cts.Token).ConfigureAwait(true);

        await PayjoinInvoiceTestHelper.AssertInvoiceProcessingThenSettledAsync(
            tester,
            payjoinContext.InvoiceId,
            async cancellationToken =>
            {
                await tester.ExplorerNode.GenerateAsync(1, cancellationToken).ConfigureAwait(true);
            },
            cts.Token).ConfigureAwait(true);

        await PayjoinInvoiceTestHelper.FinalizePayjoinPaymentAsync(tester, context.Merchant, payjoinContext, transactionId, cts.Token).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task GetBip21DoesNotEnablePayjoinWhenMerchantHasOnlyUnconfirmedCoins()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, confirmFunding: false, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var confirmedOutpoints = await PayjoinIntegrationTestSupport.GetReceiverOutpointsAsync(tester, context.Merchant.StoreId, confirmedOnly: true, cts.Token).ConfigureAwait(true);
        Assert.Empty(confirmedOutpoints);

        var allOutpoints = await PayjoinIntegrationTestSupport.GetReceiverOutpointsAsync(tester, context.Merchant.StoreId, confirmedOnly: false, cts.Token).ConfigureAwait(true);
        Assert.NotEmpty(allOutpoints);

        var (_, bip21Response) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.Merchant, cts.Token).ConfigureAwait(true);

        PayjoinIntegrationTestSupport.AssertPlainBip21(bip21Response);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task GetBip21DoesNotEnablePayjoinWhenStoreSettingDisabledDoesNotCreateReceiverSession()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.DisablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var (invoiceId, bip21Response) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.Merchant, cts.Token).ConfigureAwait(true);

        PayjoinIntegrationTestSupport.AssertPlainBip21(bip21Response);

        var sessionStore = tester.PayTester.GetService<PayjoinReceiverSessionStore>();
        Assert.False(sessionStore.TryGetSession(invoiceId, out _));
        Assert.DoesNotContain(sessionStore.GetSessions(), session => session.InvoiceId == invoiceId);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task CheckoutModelStoresMetadataAndGetBip21CreatesSessionWhenEnabled()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var invoice = await context.Merchant.BitPay.CreateInvoiceAsync(new Invoice
        {
            Price = 0.1m,
            Currency = PayjoinConstants.BitcoinCode,
            FullNotifications = true
        }).WaitAsync(cts.Token).ConfigureAwait(true);

        var checkoutModel = await PayjoinIntegrationTestSupport.GetCheckoutModelAsync(tester, invoice.Id, cts.Token).ConfigureAwait(true);

        Assert.True(checkoutModel.AdditionalData.ContainsKey(PayjoinBitcoinCheckoutModelExtension.PlainBitcoinUrlKey));
        Assert.True(checkoutModel.AdditionalData.ContainsKey(PayjoinBitcoinCheckoutModelExtension.PlainBitcoinUrlQrKey));
        Assert.True(checkoutModel.AdditionalData.ContainsKey(PayjoinBitcoinCheckoutModelExtension.PayjoinPaymentUrlEndpointKey));
        Assert.True(checkoutModel.AdditionalData.ContainsKey(PayjoinBitcoinCheckoutModelExtension.PayjoinV2EnabledKey));
        Assert.Equal(checkoutModel.InvoiceBitcoinUrl, checkoutModel.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PlainBitcoinUrlKey].ToObject<string>());
        Assert.Equal(checkoutModel.InvoiceBitcoinUrlQR, checkoutModel.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PlainBitcoinUrlQrKey].ToObject<string>());
        Assert.False(string.IsNullOrWhiteSpace(checkoutModel.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PayjoinPaymentUrlEndpointKey].ToObject<string>()));
        Assert.True(checkoutModel.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PayjoinV2EnabledKey].ToObject<bool>());
        Assert.False(checkoutModel.AdditionalData.ContainsKey(PayjoinBitcoinCheckoutModelExtension.PayjoinBitcoinUrlKey));

        var sessionStore = tester.PayTester.GetService<PayjoinReceiverSessionStore>();
        Assert.False(sessionStore.TryGetSession(invoice.Id, out _));

        var bip21Response = await PayjoinIntegrationTestSupport.GetBip21Async(tester, invoice.Id, cts.Token).ConfigureAwait(true);
        PayjoinIntegrationTestSupport.AssertPayjoinBip21(bip21Response);

        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(tester, invoice.Id, cts.Token).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task CheckoutModelStoresDisabledPayjoinMetadataWhenDisabled()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.DisablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var invoice = await context.Merchant.BitPay.CreateInvoiceAsync(new Invoice
        {
            Price = 0.1m,
            Currency = PayjoinConstants.BitcoinCode,
            FullNotifications = true
        }).WaitAsync(cts.Token).ConfigureAwait(true);

        var checkoutModel = await PayjoinIntegrationTestSupport.GetCheckoutModelAsync(tester, invoice.Id, cts.Token).ConfigureAwait(true);

        Assert.True(checkoutModel.AdditionalData.ContainsKey(PayjoinBitcoinCheckoutModelExtension.PlainBitcoinUrlKey));
        Assert.True(checkoutModel.AdditionalData.ContainsKey(PayjoinBitcoinCheckoutModelExtension.PlainBitcoinUrlQrKey));
        Assert.True(checkoutModel.AdditionalData.ContainsKey(PayjoinBitcoinCheckoutModelExtension.PayjoinPaymentUrlEndpointKey));
        Assert.True(checkoutModel.AdditionalData.ContainsKey(PayjoinBitcoinCheckoutModelExtension.PayjoinV2EnabledKey));
        Assert.Equal(checkoutModel.InvoiceBitcoinUrl, checkoutModel.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PlainBitcoinUrlKey].ToObject<string>());
        Assert.Equal(checkoutModel.InvoiceBitcoinUrlQR, checkoutModel.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PlainBitcoinUrlQrKey].ToObject<string>());
        Assert.False(string.IsNullOrWhiteSpace(checkoutModel.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PayjoinPaymentUrlEndpointKey].ToObject<string>()));
        Assert.False(checkoutModel.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PayjoinV2EnabledKey].ToObject<bool>());
        Assert.False(checkoutModel.AdditionalData.ContainsKey(PayjoinBitcoinCheckoutModelExtension.PayjoinBitcoinUrlKey));

        var sessionStore = tester.PayTester.GetService<PayjoinReceiverSessionStore>();
        Assert.False(sessionStore.TryGetSession(invoice.Id, out _));
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task GetBip21IsIdempotentForSameInvoice()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var (invoiceId, firstResponse) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.Merchant, cts.Token).ConfigureAwait(true);
        PayjoinIntegrationTestSupport.AssertPayjoinBip21(firstResponse);

        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);

        var sessionStore = tester.PayTester.GetService<PayjoinReceiverSessionStore>();
        Assert.Single(sessionStore.GetSessions(), s => s.InvoiceId == invoiceId);

        var secondResponse = await PayjoinIntegrationTestSupport.GetBip21Async(tester, invoiceId, cts.Token).ConfigureAwait(true);

        PayjoinIntegrationTestSupport.AssertPayjoinBip21(secondResponse);
        Assert.Equal(firstResponse.Bip21, secondResponse.Bip21);
        Assert.Single(sessionStore.GetSessions(), s => s.InvoiceId == invoiceId);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task GetBip21RebuildsPersistedReceiverSessionWhenEventLogIsEmpty()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var (invoiceId, _) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.Merchant, cts.Token).ConfigureAwait(true);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);

        var sessionStore = tester.PayTester.GetService<PayjoinReceiverSessionStore>();
        Assert.True(sessionStore.RemoveSession(invoiceId));

        var invoiceRepository = tester.PayTester.GetService<InvoiceRepository>();
        var invoice = await invoiceRepository.GetInvoice(invoiceId).WaitAsync(cts.Token).ConfigureAwait(true);
        Assert.NotNull(invoice);

        var storeSettingsRepository = tester.PayTester.GetService<IPayjoinStoreSettingsRepository>();
        var storeSettings = await storeSettingsRepository.GetAsync(invoice!.StoreId).WaitAsync(cts.Token).ConfigureAwait(true);
        Assert.NotNull(storeSettings?.OhttpRelayUrl);

        var prompt = invoice.GetPaymentPrompt(Payments.PaymentTypes.CHAIN.GetPaymentMethodId("BTC"));
        Assert.NotNull(prompt?.Destination);

        var pluginDbContextFactory = tester.PayTester.GetService<PayjoinPluginDbContextFactory>();
        using (var db = pluginDbContextFactory.CreateContext())
        {
            var now = DateTimeOffset.UtcNow;
            db.ReceiverSessions.Add(new PayjoinReceiverSessionData
            {
                InvoiceId = invoiceId,
                StoreId = invoice.StoreId,
                ReceiverAddress = prompt.Destination,
                OhttpRelayUrl = storeSettings!.OhttpRelayUrl!.AbsoluteUri,
                MonitoringExpiresAt = invoice.MonitoringExpiration,
                CreatedAt = now,
                UpdatedAt = now
            });
            db.SaveChanges();
        }

        var rebuiltResponse = await PayjoinIntegrationTestSupport.GetBip21Async(tester, invoiceId, cts.Token).ConfigureAwait(true);

        PayjoinIntegrationTestSupport.AssertPayjoinBip21(rebuiltResponse);

        var rebuiltSession = PayjoinReceiverTestHelper.GetRequiredReceiverSession(tester, invoiceId);
        Assert.NotEmpty(rebuiltSession.GetEvents());
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task GetBip21DoesNotEnablePayjoinWhenStoreSettingDisabled()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.DisablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var (_, bip21Response) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.Merchant, cts.Token).ConfigureAwait(true);

        PayjoinIntegrationTestSupport.AssertPlainBip21(bip21Response);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task GetBip21DoesNotEnablePayjoinWhenOhttpRelayUrlMissing()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, settings =>
        {
            settings.OhttpRelayUrl = null;
        }, cts.Token).ConfigureAwait(true);

        var (_, bip21Response) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.Merchant, cts.Token).ConfigureAwait(true);

        PayjoinIntegrationTestSupport.AssertPlainBip21(bip21Response);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task GetBip21DoesNotEnablePayjoinWhenDirectoryUrlMissing()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, settings =>
        {
            settings.DirectoryUrl = null;
        }, cts.Token).ConfigureAwait(true);

        var (_, bip21Response) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.Merchant, cts.Token).ConfigureAwait(true);

        PayjoinIntegrationTestSupport.AssertPlainBip21(bip21Response);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task GetBip21FallsBackToPlainBip21WhenOhttpKeysFetchFails()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, settings =>
        {
            settings.OhttpRelayUrl = new Uri("http://127.0.0.1:1/");
        }, cts.Token).ConfigureAwait(true);

        var (_, bip21Response) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.Merchant, cts.Token).ConfigureAwait(true);

        PayjoinIntegrationTestSupport.AssertPlainBip21(bip21Response);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task ReceiverSessionIsRemovedWhenInvoiceGetsPaidWithoutPayjoin()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var (invoiceId, bip21Response) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.Merchant, cts.Token).ConfigureAwait(true);
        PayjoinIntegrationTestSupport.AssertPayjoinBip21(bip21Response);

        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);

        await context.Merchant.PayInvoice(invoiceId).WaitAsync(cts.Token).ConfigureAwait(true);

        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyRemovedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task ReceiverSessionIsRemovedWhenInvoiceExpires()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var (invoiceId, bip21Response) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.Merchant, cts.Token).ConfigureAwait(true);
        PayjoinIntegrationTestSupport.AssertPayjoinBip21(bip21Response);

        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);

        var invoiceRepository = tester.PayTester.GetService<InvoiceRepository>();
        await invoiceRepository.UpdateInvoiceExpiry(invoiceId, TimeSpan.Zero).WaitAsync(cts.Token).ConfigureAwait(true);

        await PayjoinInvoiceTestHelper.AssertInvoiceStatusEventuallyAsync(tester, invoiceId, InvoiceStatus.Expired, cts.Token).ConfigureAwait(true);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyRemovedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task ReceiverSessionIsRemovedWhenInvoiceBecomesInvalid()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var (invoiceId, bip21Response) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.Merchant, cts.Token).ConfigureAwait(true);
        PayjoinIntegrationTestSupport.AssertPayjoinBip21(bip21Response);

        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);

        var invoiceRepository = tester.PayTester.GetService<InvoiceRepository>();
        var marked = await invoiceRepository.MarkInvoiceStatus(invoiceId, InvoiceStatus.Invalid).WaitAsync(cts.Token).ConfigureAwait(true);
        Assert.True(marked);

        await PayjoinInvoiceTestHelper.AssertInvoiceStatusEventuallyAsync(tester, invoiceId, InvoiceStatus.Invalid, cts.Token).ConfigureAwait(true);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyRemovedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task PayjoinRequestFailsWhenInvoiceWasAlreadyPaid()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);
        var payer = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(tester, context.Network, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var (invoiceId, bip21Response) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.Merchant, cts.Token).ConfigureAwait(true);
        PayjoinIntegrationTestSupport.AssertPayjoinBip21(bip21Response);

        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);

        await context.Merchant.PayInvoice(invoiceId).WaitAsync(cts.Token).ConfigureAwait(true);
        await context.Merchant.WaitInvoicePaid(invoiceId).WaitAsync(cts.Token).ConfigureAwait(true);

        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCloseRequestedOrRemovedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PayjoinIntegrationTestSupport.PayInvoiceViaExternalPayjoinPayerAsync(
                tester,
                payer,
                context.Network,
                context.Merchant.StoreId,
                new Uri(bip21Response.Bip21, UriKind.Absolute),
                cts.Token)).ConfigureAwait(true);

        Assert.Contains("rejected", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("timed out", exception.Message, StringComparison.OrdinalIgnoreCase);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyRemovedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task PayjoinRequestFailsWhenInvoiceWasExpired()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);
        var payer = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(tester, context.Network, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var (invoiceId, bip21Response) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.Merchant, cts.Token).ConfigureAwait(true);
        PayjoinIntegrationTestSupport.AssertPayjoinBip21(bip21Response);

        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);

        var invoiceRepository = tester.PayTester.GetService<InvoiceRepository>();
        await invoiceRepository.UpdateInvoiceExpiry(invoiceId, TimeSpan.Zero).WaitAsync(cts.Token).ConfigureAwait(true);

        await PayjoinInvoiceTestHelper.AssertInvoiceStatusEventuallyAsync(tester, invoiceId, InvoiceStatus.Expired, cts.Token).ConfigureAwait(true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PayjoinIntegrationTestSupport.PayInvoiceViaExternalPayjoinPayerAsync(
                tester,
                payer,
                context.Network,
                context.Merchant.StoreId,
                new Uri(bip21Response.Bip21, UriKind.Absolute),
                cts.Token)).ConfigureAwait(true);

        Assert.Contains("rejected", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("timed out", exception.Message, StringComparison.OrdinalIgnoreCase);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyRemovedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task PayjoinRequestFailsWhenInvoiceBecomesInvalid()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);
        var payer = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(tester, context.Network, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.Merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var (invoiceId, bip21Response) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.Merchant, cts.Token).ConfigureAwait(true);
        PayjoinIntegrationTestSupport.AssertPayjoinBip21(bip21Response);

        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);

        var invoiceRepository = tester.PayTester.GetService<InvoiceRepository>();
        var marked = await invoiceRepository.MarkInvoiceStatus(invoiceId, InvoiceStatus.Invalid).WaitAsync(cts.Token).ConfigureAwait(true);
        Assert.True(marked);

        await PayjoinInvoiceTestHelper.AssertInvoiceStatusEventuallyAsync(tester, invoiceId, InvoiceStatus.Invalid, cts.Token).ConfigureAwait(true);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCloseRequestedOrRemovedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PayjoinIntegrationTestSupport.PayInvoiceViaExternalPayjoinPayerAsync(
                tester,
                payer,
                context.Network,
                context.Merchant.StoreId,
                new Uri(bip21Response.Bip21, UriKind.Absolute),
                cts.Token)).ConfigureAwait(true);

        Assert.Contains("rejected", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("timed out", exception.Message, StringComparison.OrdinalIgnoreCase);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyRemovedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);
    }

    private static async Task RestartPayServerAsync(ServerTester tester, CancellationToken cancellationToken)
    {
        tester.PayTester.Dispose();
        await tester.StartAsync().WaitAsync(cancellationToken).ConfigureAwait(true);
    }

    private static async Task<(string InvoiceId, PaymentMethodId PaymentMethodId, decimal ExactDue)> CreateSettledOverpaidInvoiceAsync(
        ServerTester tester,
        TestAccount merchant,
        BTCPayNetwork network,
        CancellationToken cancellationToken)
    {
        var invoice = await merchant.BitPay.CreateInvoiceAsync(new Invoice
        {
            Price = 0.1m,
            Currency = PayjoinConstants.BitcoinCode,
            FullNotifications = true
        }).WaitAsync(cancellationToken).ConfigureAwait(true);

        var invoiceRepository = tester.PayTester.GetService<InvoiceRepository>();
        var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(PayjoinConstants.BitcoinCode);
        var invoiceEntity = await invoiceRepository.GetInvoice(invoice.Id).WaitAsync(cancellationToken).ConfigureAwait(true);
        Assert.NotNull(invoiceEntity);

        var prompt = invoiceEntity!.GetPaymentPrompt(paymentMethodId);
        Assert.NotNull(prompt);

        var exactDue = prompt!.Calculate().Due;
        var destination = BitcoinAddress.Create(prompt.Destination, network.NBitcoinNetwork);
        await tester.ExplorerNode.SendToAddressAsync(destination, Money.Coins(exactDue) + Money.Satoshis(1_000), cancellationToken).ConfigureAwait(true);
        await tester.ExplorerNode.GenerateAsync(1, cancellationToken).ConfigureAwait(true);

        await AssertInvoiceStateEventuallyAsync(
            tester,
            invoice.Id,
            InvoiceStatus.Settled,
            InvoiceExceptionStatus.PaidOver,
            cancellationToken).ConfigureAwait(true);

        return (invoice.Id, paymentMethodId, exactDue);
    }

    private static async Task<InvoiceEntity> AssertInvoiceStateEventuallyAsync(
        ServerTester tester,
        string invoiceId,
        InvoiceStatus expectedStatus,
        InvoiceExceptionStatus expectedExceptionStatus,
        CancellationToken cancellationToken)
    {
        var invoiceRepository = tester.PayTester.GetService<InvoiceRepository>();

        for (var attempt = 0; attempt < 120; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var invoice = await invoiceRepository.GetInvoice(invoiceId).WaitAsync(cancellationToken).ConfigureAwait(true);
            if (invoice is not null)
            {
                var state = invoice.GetInvoiceState();
                if (state.Status == expectedStatus && state.ExceptionStatus == expectedExceptionStatus)
                {
                    return invoice;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(true);
        }

        Assert.Fail($"Expected invoice '{invoiceId}' state to become '{expectedStatus}' with exception '{expectedExceptionStatus}'.");
        return null!;
    }

}
