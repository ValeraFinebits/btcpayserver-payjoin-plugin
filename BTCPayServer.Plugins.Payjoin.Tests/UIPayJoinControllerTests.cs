using BTCPayServer.Plugins.Payjoin.Controllers;
using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services;
using Microsoft.AspNetCore.Mvc;
using NBitcoin;
using System.Globalization;
using System.Runtime.CompilerServices;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class UIPayJoinControllerTests
{
    private static UIPayJoinController CreateController(bool cheatMode)
    {
        return new UIPayJoinController(CreateEnvironment(cheatMode), null!, null!, null!, null!, null!, null!);
    }

    private static RunTestPaymentResponse AssertRunTestPaymentFailure(ActionResult<RunTestPaymentResponse> actionResult, string expectedMessage)
    {
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var response = Assert.IsType<RunTestPaymentResponse>(okResult.Value);
        Assert.False(response.Succeeded);
        Assert.Equal(expectedMessage, response.Message);
        Assert.Null(response.TransactionId);
        return response;
    }

    private static BTCPayServerEnvironment CreateEnvironment(bool cheatMode)
    {
        var env = (BTCPayServerEnvironment)RuntimeHelpers.GetUninitializedObject(typeof(BTCPayServerEnvironment));
        typeof(BTCPayServerEnvironment).GetProperty("CheatMode")?.SetValue(env, cheatMode);
        return env;
    }

    [Fact]
    public async Task RunTestPaymentReturnsNotFoundWhenCheatModeDisabled()
    {
        using var controller = CreateController(false);

        var result = await controller.RunTestPayment(new RunTestPaymentRequest
        {
            InvoiceId = "invoice-1"
        }, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task RunTestPaymentThrowsWhenRequestIsNull()
    {
        using var controller = CreateController(true);

        await Assert.ThrowsAsync<ArgumentNullException>(() => controller.RunTestPayment(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunTestPaymentReturnsFailureWhenInvoiceIdMissing()
    {
        using var controller = CreateController(true);

        var result = await controller.RunTestPayment(new RunTestPaymentRequest(), TestContext.Current.CancellationToken);

        AssertRunTestPaymentFailure(result, "invoiceId is required");
    }

    [Fact]
    public async Task RunTestPaymentReturnsFailureWhenPaymentUrlMissing()
    {
        using var controller = CreateController(true);

        var result = await controller.RunTestPayment(new RunTestPaymentRequest
        {
            InvoiceId = "invoice-1"
        }, TestContext.Current.CancellationToken);

        AssertRunTestPaymentFailure(result, "paymentUrl is required");
    }

    [Fact]
    public void MapRunTestPaymentExceptionReturnsInvariantFailureResponse()
    {
        using var controller = CreateController(true);

        var result = controller.MapRunTestPaymentException("invoice-1", new SelfPayInvariantChecker.SelfPayInvariantException("receiver reused sender change"));

        AssertRunTestPaymentFailure(result, "Self-pay invariant failed: receiver reused sender change");
    }

    [Fact]
    public void MapRunTestPaymentExceptionReturnsExecutionFailureResponse()
    {
        using var controller = CreateController(true);

        var result = controller.MapRunTestPaymentException("invoice-1", new RunTestPaymentService.RunTestPaymentExecutionException("wallet funds too low for fees"));

        AssertRunTestPaymentFailure(result, "wallet funds too low for fees");
    }

    [Fact]
    public void CaptureSelfPaySenderContextThrowsWhenSenderTransactionHasNoChangeOutput()
    {
        var invoiceScript = CreateScript(1);
        var senderTransaction = CreateTransaction(
            [CreateInput(1)],
            [new TxOut(Money.Satoshis(10_000_000), invoiceScript)]);

        var exception = Assert.Throws<SelfPayInvariantChecker.SelfPayInvariantException>(() =>
            SelfPayInvariantChecker.CreateBaseline(senderTransaction, invoiceScript, Money.Satoshis(10_000_000)));

        Assert.Contains("must contain at least one non-invoice output", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateSelfPayProposalThrowsWhenSenderChangeScriptIsReused()
    {
        var invoiceScript = CreateScript(2);
        var changeScript = CreateScript(3);
        var senderTransaction = CreateTransaction(
            [CreateInput(2), CreateInput(3)],
            [
                new TxOut(Money.Satoshis(10_000_000), invoiceScript),
                new TxOut(Money.Satoshis(189_999_760), changeScript)
            ]);
        var senderContext = SelfPayInvariantChecker.CreateBaseline(senderTransaction, invoiceScript, Money.Satoshis(10_000_000));
        var proposalTransaction = CreateTransaction(
            [CreateInput(2), CreateInput(3), CreateInput(4)],
            [
                new TxOut(Money.Satoshis(10_000_000), invoiceScript),
                new TxOut(Money.Satoshis(189_999_760), changeScript),
                new TxOut(Money.Satoshis(500), changeScript)
            ]);

        var exception = Assert.Throws<SelfPayInvariantChecker.SelfPayInvariantException>(() =>
            SelfPayInvariantChecker.ValidateProposal(proposalTransaction, senderContext));

        Assert.Contains("reused a sender change script", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateSelfPayFinalTransactionThrowsWhenDustOutputExists()
    {
        var invoiceScript = CreateScript(4);
        var changeScript = CreateScript(5);
        var senderTransaction = CreateTransaction(
            [CreateInput(5), CreateInput(6)],
            [
                new TxOut(Money.Satoshis(10_000_000), invoiceScript),
                new TxOut(Money.Satoshis(189_999_760), changeScript)
            ]);
        var senderContext = SelfPayInvariantChecker.CreateBaseline(senderTransaction, invoiceScript, Money.Satoshis(10_000_000));
        var finalTransaction = CreateTransaction(
            [CreateInput(5), CreateInput(6), CreateInput(7)],
            [
                new TxOut(Money.Satoshis(10_000_000), invoiceScript),
                new TxOut(Money.Satoshis(189_999_760), CreateScript(6)),
                new TxOut(Money.Satoshis(1), CreateScript(7))
            ]);

        var exception = Assert.Throws<SelfPayInvariantChecker.SelfPayInvariantException>(() =>
            SelfPayInvariantChecker.ValidateFinalTransaction(finalTransaction, senderContext));

        Assert.Contains("contains a dust output", exception.Message, StringComparison.Ordinal);
    }

    private static Transaction CreateTransaction(TxIn[] inputs, TxOut[] outputs)
    {
        var transaction = Network.RegTest.CreateTransaction();
        foreach (var input in inputs)
        {
            transaction.Inputs.Add(input);
        }

        foreach (var output in outputs)
        {
            transaction.Outputs.Add(output);
        }

        return transaction;
    }

    private static TxIn CreateInput(int seed)
    {
        var hex = seed.ToString("x2", CultureInfo.InvariantCulture);
        var txId = string.Concat(Enumerable.Repeat(hex, 32));
        return new TxIn(new OutPoint(uint256.Parse(txId), 0));
    }

    private static Script CreateScript(int seed)
    {
        var keyBytes = Enumerable.Repeat((byte)seed, 32).ToArray();
        using var key = new Key(keyBytes);
        return key.PubKey.WitHash.ScriptPubKey;
    }
}
