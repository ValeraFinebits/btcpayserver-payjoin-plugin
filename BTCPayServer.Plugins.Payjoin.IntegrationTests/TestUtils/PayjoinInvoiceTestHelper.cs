using BTCPayServer.Client.Models;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Tests;
using NBitcoin;
using NBitpayClient;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;

internal static class PayjoinInvoiceTestHelper
{
    private const string BitcoinCode = "BTC";
    private const decimal InvoicePrice = 0.1m;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan InvoiceStatusTimeout = TimeSpan.FromSeconds(30);

    public static async Task<PayjoinInvoiceContext> PreparePayjoinInvoiceAsync(
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

        var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(BitcoinCode);
        var invoiceBeforePayment = await tester.PayTester.GetService<InvoiceRepository>().GetInvoice(invoice.Id).WaitAsync(cancellationToken).ConfigureAwait(true);
        Assert.NotNull(invoiceBeforePayment);

        var promptBeforePayment = invoiceBeforePayment.GetPaymentPrompt(paymentMethodId);
        Assert.NotNull(promptBeforePayment);

        var expectedDue = promptBeforePayment.Calculate().Due;
        var receiverOutpointsBeforePayment = await PayjoinIntegrationTestSupport.GetReceiverOutpointsAsync(tester, merchant.StoreId, confirmedOnly: true, cancellationToken).ConfigureAwait(true);

        var paymentUrlService = tester.PayTester.GetService<PayjoinInvoicePaymentUrlService>();
        var bip21Response = await paymentUrlService.GetInvoicePaymentUrlAsync(invoice.Id, cancellationToken).ConfigureAwait(true);
        Assert.NotNull(bip21Response);
        PayjoinIntegrationTestSupport.AssertPayjoinBip21(bip21Response);

        var paymentUrl = new Uri(bip21Response.Bip21, UriKind.Absolute);
        var storeSettings = await tester.PayTester.GetService<IPayjoinStoreSettingsRepository>().GetAsync(merchant.StoreId).WaitAsync(cancellationToken).ConfigureAwait(true);
        Assert.NotNull(storeSettings.DirectoryUrl);
        Assert.NotNull(storeSettings.OhttpRelayUrl);

        var invoiceScript = BitcoinAddress.Create(promptBeforePayment.Destination, network.NBitcoinNetwork).ScriptPubKey;
        return new PayjoinInvoiceContext(
            invoice.Id,
            paymentMethodId,
            expectedDue,
            receiverOutpointsBeforePayment,
            paymentUrl,
            storeSettings.DirectoryUrl,
            storeSettings.OhttpRelayUrl,
            invoiceScript);
    }

    public static async Task<(Transaction PayjoinTransaction, Script InvoiceScript, string TransactionId)> FinalizePayjoinPaymentAsync(
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

    private static void AssertHasReceiverContribution(Transaction payjoinTx, HashSet<string> receiverOutpointsBeforePayment)
    {
        var receiverContributionFound = payjoinTx.Inputs.Any(input => receiverOutpointsBeforePayment.Contains(input.PrevOut.ToString()));
        Assert.True(receiverContributionFound,
            $"Expected payjoin tx to spend at least one receiver-owned coin. Inputs: {string.Join(", ", payjoinTx.Inputs.Select(input => input.PrevOut))}");
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

    internal sealed record PayjoinInvoiceContext(
        string InvoiceId,
        PaymentMethodId PaymentMethodId,
        decimal ExpectedDue,
        HashSet<string> ReceiverOutpointsBeforePayment,
        Uri PaymentUrl,
        Uri DirectoryUrl,
        Uri OhttpRelayUrl,
        Script InvoiceScript);
}
