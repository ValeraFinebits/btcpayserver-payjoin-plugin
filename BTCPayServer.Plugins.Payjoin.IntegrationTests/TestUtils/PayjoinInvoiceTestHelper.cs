using BTCPayServer.Client.Models;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Tests;
using NBitcoin;
using NBitpayClient;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;

internal static class PayjoinInvoiceTestHelper
{
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
            Currency = PayjoinConstants.BitcoinCode,
            FullNotifications = true
        }).WaitAsync(cancellationToken).ConfigureAwait(true);

        var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(PayjoinConstants.BitcoinCode);
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

        var invoiceEntity = await AssertInvoiceAccountedEventuallyAsync(tester, payjoinContext, cancellationToken).ConfigureAwait(true);
        await AssertInvoiceStatusEventuallyAsync(tester, payjoinContext.InvoiceId, InvoiceStatus.Settled, cancellationToken).ConfigureAwait(true);

        Assert.NotNull(invoiceEntity);

        var payments = invoiceEntity
            .GetPayments(false)
            .Where(p => p.Accounted && p.PaymentMethodId == payjoinContext.PaymentMethodId)
            .ToList();
        Assert.Single(payments);

        await AssertAccountedPaymentMatchesFinalPayjoinTransactionEventuallyAsync(tester, payjoinTx, payjoinContext, transactionId, cancellationToken).ConfigureAwait(true);

        var totalPaid = payments.Sum(p => p.Value);
        Assert.Equal(payjoinContext.ExpectedDue, totalPaid);

        return (payjoinTx, payjoinContext.InvoiceScript, transactionId);
    }

    public static async Task AssertInvoiceNotOverpaidEventuallyAsync(
        ServerTester tester,
        PayjoinInvoiceContext payjoinContext,
        CancellationToken cancellationToken)
    {
        var invoiceRepository = tester.PayTester.GetService<InvoiceRepository>();

        for (var attempt = 0; attempt < GetAttemptCount(InvoiceStatusTimeout); attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var invoice = await invoiceRepository.GetInvoice(payjoinContext.InvoiceId).WaitAsync(cancellationToken).ConfigureAwait(true);
            if (invoice is not null)
            {
                var accountedPayments = invoice.GetPayments(false)
                    .Where(p => p.Accounted && p.PaymentMethodId == payjoinContext.PaymentMethodId)
                    .ToList();
                var totalPaid = accountedPayments.Sum(p => p.Value);
                var state = invoice.GetInvoiceState();

                if (totalPaid == payjoinContext.ExpectedDue &&
                    state.Status is InvoiceStatus.Processing or InvoiceStatus.Settled &&
                    state.ExceptionStatus != InvoiceExceptionStatus.PaidOver)
                {
                    return;
                }
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(true);
        }

        Assert.Fail($"Expected invoice '{payjoinContext.InvoiceId}' to remain not overpaid while accounting exactly '{payjoinContext.ExpectedDue}'.");
    }

    public static async Task AssertInvoiceProcessingThenSettledAsync(
        ServerTester tester,
        string invoiceId,
        Func<CancellationToken, Task> confirmFinalTransactionAsync,
        CancellationToken cancellationToken)
    {
        await AssertInvoiceStatusEventuallyAsync(tester, invoiceId, InvoiceStatus.Processing, cancellationToken).ConfigureAwait(true);
        await confirmFinalTransactionAsync(cancellationToken).ConfigureAwait(true);
        await AssertInvoiceStatusEventuallyAsync(tester, invoiceId, InvoiceStatus.Settled, cancellationToken).ConfigureAwait(true);
    }

    private static async Task<InvoiceEntity> AssertInvoiceAccountedEventuallyAsync(
        ServerTester tester,
        PayjoinInvoiceContext payjoinContext,
        CancellationToken cancellationToken)
    {
        var invoiceRepository = tester.PayTester.GetService<InvoiceRepository>();

        for (var attempt = 0; attempt < GetAttemptCount(InvoiceStatusTimeout); attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var invoice = await invoiceRepository.GetInvoice(payjoinContext.InvoiceId).WaitAsync(cancellationToken).ConfigureAwait(true);
            if (invoice is not null)
            {
                var totalPaid = invoice.GetPayments(false)
                    .Where(p => p.Accounted && p.PaymentMethodId == payjoinContext.PaymentMethodId)
                    .Sum(p => p.Value);
                if (totalPaid == payjoinContext.ExpectedDue)
                {
                    return invoice;
                }
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(true);
        }

        Assert.Fail($"Expected invoice '{payjoinContext.InvoiceId}' to become accounted for '{payjoinContext.ExpectedDue}'.");
        return null!;
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

    private static async Task AssertAccountedPaymentMatchesFinalPayjoinTransactionEventuallyAsync(
        ServerTester tester,
        Transaction payjoinTx,
        PayjoinInvoiceContext payjoinContext,
        string transactionId,
        CancellationToken cancellationToken)
    {
        var invoiceRepository = tester.PayTester.GetService<InvoiceRepository>();
        var accountingBridgeService = tester.PayTester.GetService<IPayjoinAccountingBridgeService>();
        var bitcoinHandler = tester.PayTester.GetService<PaymentMethodHandlerDictionary>().GetBitcoinHandler(PayjoinConstants.BitcoinCode);
        var expectedTransactionId = uint256.Parse(transactionId);

        for (var attempt = 0; attempt < GetAttemptCount(InvoiceStatusTimeout); attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var invoice = await invoiceRepository.GetInvoice(payjoinContext.InvoiceId).WaitAsync(cancellationToken).ConfigureAwait(true);
            var bridge = await accountingBridgeService.TryGetByInvoiceIdAsync(payjoinContext.InvoiceId, cancellationToken).ConfigureAwait(true);
            var accountedPayment = invoice?
                .GetPayments(false)
                .SingleOrDefault(p => p.Accounted && p.PaymentMethodId == payjoinContext.PaymentMethodId);
            var accountedPaymentData = accountedPayment?.GetDetails<BitcoinLikePaymentData>(bitcoinHandler);

            if (accountedPaymentData is not null &&
                bridge is not null &&
                accountedPaymentData.Outpoint.Hash == expectedTransactionId &&
                IsReceiverSettlementOutput(accountedPaymentData.Outpoint, payjoinTx, bridge))
            {
                Assert.True(accountedPaymentData.Outpoint.N < payjoinTx.Outputs.Count,
                    $"Expected accounted payjoin payment to reference an existing final transaction output. Outpoint={accountedPaymentData.Outpoint}, Outputs={FormatOutputs(payjoinTx)}");
                return;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(true);
        }

        Assert.Fail($"Expected invoice '{payjoinContext.InvoiceId}' accounting to reconcile to final payjoin transaction '{transactionId}'. Outputs={FormatOutputs(payjoinTx)}");
    }

    private static bool IsReceiverSettlementOutput(OutPoint outPoint, Transaction payjoinTx, PayjoinAccountingBridgeState bridge)
    {
        if (outPoint.N >= payjoinTx.Outputs.Count)
        {
            return false;
        }

        if (bridge.ExpectedFinalOutputIndex.HasValue && bridge.ExpectedFinalOutputIndex.Value != outPoint.N)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(bridge.SettlementScript))
        {
            var settlementScript = Script.FromBytesUnsafe(Convert.FromHexString(bridge.SettlementScript));
            return payjoinTx.Outputs[(int)outPoint.N].ScriptPubKey == settlementScript;
        }

        return true;
    }

    private static string FormatOutputs(Transaction transaction)
    {
        return string.Join(", ", transaction.Outputs.Select((output, index) =>
            $"#{index}:value={output.Value.Satoshi}sats:script={output.ScriptPubKey}"));
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
