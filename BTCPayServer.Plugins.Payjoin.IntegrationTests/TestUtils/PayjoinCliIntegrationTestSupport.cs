using BTCPayServer.Tests;
using NBitcoin;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;

internal static class PayjoinCliIntegrationTestSupport
{
    public static async Task<(string InvoiceId, Transaction PayjoinTransaction, Script InvoiceScript, string TransactionId)> CreateAndPayInvoiceWithInvoiceIdAsync(
        ServerTester tester,
        TestAccount merchant,
        BTCPayNetwork network,
        CancellationToken cancellationToken)
    {
        return await CreateAndPayInvoiceWithInvoiceIdAsync(tester, merchant, network, options: null, cancellationToken: cancellationToken).ConfigureAwait(true);
    }

    public static async Task<(string InvoiceId, Transaction PayjoinTransaction, Script InvoiceScript, string TransactionId)> CreateAndPayInvoiceWithInvoiceIdAsync(
        ServerTester tester,
        TestAccount merchant,
        BTCPayNetwork network,
        BitcoindNodeOptions? options,
        CancellationToken cancellationToken)
    {
        var payjoinContext = await PayjoinInvoiceTestHelper.PreparePayjoinInvoiceAsync(tester, merchant, network, cancellationToken).ConfigureAwait(true);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(tester, payjoinContext.InvoiceId, cancellationToken).ConfigureAwait(true);

        var receiverDiagnosticsBeforeSend = await PayjoinReceiverTestHelper.GetReceiverSideDiagnosticsAsync(tester, payjoinContext.InvoiceId, cancellationToken).ConfigureAwait(true);
        using var senderWallet = await PayjoinCliSenderWallet.CreateInitializedAsync(tester, network, options, cancellationToken).ConfigureAwait(true);
        using var payjoinCliPayer = new PayjoinCliPayer(senderWallet);
        PayjoinCliPaymentResult paymentResult;
        try
        {
            paymentResult = await payjoinCliPayer.PayAsync(
                payjoinContext.PaymentUrl,
                payjoinContext.DirectoryUrl,
                payjoinContext.OhttpRelayUrl,
                payjoinContext.InvoiceScript,
                Money.Coins(payjoinContext.ExpectedDue),
                cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            var receiverDiagnosticsAfterFailure = await PayjoinReceiverTestHelper.TryGetReceiverSideDiagnosticsAsync(tester, payjoinContext.InvoiceId).ConfigureAwait(true);
            throw new InvalidOperationException(
                $"payjoin-cli send failed for invoice '{payjoinContext.InvoiceId}'. ReceiverBeforeSend='{receiverDiagnosticsBeforeSend}'. ReceiverAfterFailure='{receiverDiagnosticsAfterFailure}'.",
                ex);
        }

        try
        {
            await senderWallet.MineBlockAsync(cancellationToken).ConfigureAwait(true);
            await senderWallet.WaitForPrimaryNodeSyncAsync(tester, cancellationToken).ConfigureAwait(true);

            var finalizedPayment = await PayjoinInvoiceTestHelper.FinalizePayjoinPaymentAsync(tester, merchant, payjoinContext, paymentResult.TransactionId, cancellationToken).ConfigureAwait(true);
            return (payjoinContext.InvoiceId, finalizedPayment.PayjoinTransaction, finalizedPayment.InvoiceScript, finalizedPayment.TransactionId);
        }
        catch (Exception ex)
        {
            var receiverDiagnosticsAfterFailure = await PayjoinReceiverTestHelper.TryGetReceiverSideDiagnosticsAsync(tester, payjoinContext.InvoiceId).ConfigureAwait(true);
            throw new InvalidOperationException(
                $"payjoin-cli post-send flow failed for invoice '{payjoinContext.InvoiceId}' with transaction '{paymentResult.TransactionId}'. ReceiverBeforeSend='{receiverDiagnosticsBeforeSend}'. ReceiverAfterFailure='{receiverDiagnosticsAfterFailure}'. CliStdout='{DockerRunner.EscapeMultiline(paymentResult.StandardOutput)}'. CliStderr='{DockerRunner.EscapeMultiline(paymentResult.StandardError)}'.",
                ex);
        }
    }

    public static async Task<(Transaction PayjoinTransaction, Script InvoiceScript, string TransactionId)> CreateAndPayInvoiceAsync(
        ServerTester tester,
        TestAccount merchant,
        BTCPayNetwork network,
        CancellationToken cancellationToken)
    {
        return await CreateAndPayInvoiceAsync(tester, merchant, network, options: null, cancellationToken: cancellationToken).ConfigureAwait(true);
    }

    public static async Task<(Transaction PayjoinTransaction, Script InvoiceScript, string TransactionId)> CreateAndPayInvoiceAsync(
        ServerTester tester,
        TestAccount merchant,
        BTCPayNetwork network,
        BitcoindNodeOptions? options,
        CancellationToken cancellationToken)
    {
        var paymentResult = await CreateAndPayInvoiceWithInvoiceIdAsync(tester, merchant, network, options, cancellationToken).ConfigureAwait(true);
        return (paymentResult.PayjoinTransaction, paymentResult.InvoiceScript, paymentResult.TransactionId);
    }
}
