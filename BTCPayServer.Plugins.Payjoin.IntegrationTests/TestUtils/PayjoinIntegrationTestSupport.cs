using BTCPayServer.Data;
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

    public static TimeSpan TestTimeout { get; } = TimeSpan.FromMinutes(3);

    public static void AssertSuccessfulPayjoinTransaction((Transaction PayjoinTransaction, Script InvoiceScript, string TransactionId) paymentResult)
    {
        Assert.Equal(uint256.Parse(paymentResult.TransactionId), paymentResult.PayjoinTransaction.GetHash());
        Assert.True(paymentResult.PayjoinTransaction.Inputs.Count > 1,
            $"Expected payjoin tx to contain multiple inputs. Inputs: {paymentResult.PayjoinTransaction.Inputs.Count}");

        Assert.Single(paymentResult.PayjoinTransaction.Outputs, output => output.ScriptPubKey == paymentResult.InvoiceScript);
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
        var payjoinContext = await PayjoinInvoiceTestHelper.PreparePayjoinInvoiceAsync(tester, user, network, cancellationToken).ConfigureAwait(true);

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

        return await PayjoinInvoiceTestHelper.FinalizePayjoinPaymentAsync(tester, user, payjoinContext, paymentResponse.TransactionId!, cancellationToken).ConfigureAwait(true);
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
        var payjoinContext = await PayjoinInvoiceTestHelper.PreparePayjoinInvoiceAsync(tester, merchant, network, cancellationToken).ConfigureAwait(true);
        var payjoinPayer = new PayjoinTestPayer(tester, payer, network);
        var paymentResult = await payjoinPayer.PayAsync(payjoinContext.PaymentUrl, payjoinContext.OhttpRelayUrl, preProposalPollDelay, cancellationToken).ConfigureAwait(true);

        return await PayjoinInvoiceTestHelper.FinalizePayjoinPaymentAsync(tester, merchant, payjoinContext, paymentResult.TransactionId, cancellationToken).ConfigureAwait(true);
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

    private static BTCPayNetwork GetBitcoinNetwork(ServerTester tester)
    {
        var network = tester.NetworkProvider.GetNetwork<BTCPayNetwork>(BitcoinCode);
        Assert.NotNull(network);
        return network;
    }

    public static async Task<HashSet<string>> GetReceiverOutpointsAsync(ServerTester tester, string storeId, bool confirmedOnly, CancellationToken cancellationToken)
    {
        var network = GetBitcoinNetwork(tester);

        var store = await tester.PayTester.GetService<StoreRepository>().FindStore(storeId).WaitAsync(cancellationToken).ConfigureAwait(true);
        Assert.NotNull(store);

        var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(BitcoinCode);
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

}
