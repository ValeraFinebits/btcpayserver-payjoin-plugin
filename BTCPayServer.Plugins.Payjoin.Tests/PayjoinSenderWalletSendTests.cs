using BTCPayServer.Plugins.Payjoin.Controllers;
using BTCPayServer.Plugins.Wallets.Views.ViewModels;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

/// <summary>
/// The rules that decide whether a transaction built on BTCPay's send screen can become an async
/// payjoin. The screen allows more than the protocol does, so this is where the difference is
/// explained to the operator rather than failing later in the session.
/// </summary>
public class PayjoinSenderWalletSendTests
{
    private const string PayjoinUri = "bitcoin:tb1q?amount=0.001&pj=https://example.test/#K1";

    [Fact]
    public void ADestinationWithoutPayjoinIsRefused()
    {
        var model = CreateModel(payJoinBip21: null);

        Assert.Null(UIPayjoinSenderController.ResolveSingleDestination(model, out var error));
        Assert.Equal("This destination does not advertise async payjoin.", error);
    }

    [Fact]
    public void ASingleDestinationIsAccepted()
    {
        var model = CreateModel();

        var destination = UIPayjoinSenderController.ResolveSingleDestination(model, out var error);

        Assert.NotNull(destination);
        Assert.Null(error);
        Assert.Equal("tb1qdestination", destination!.DestinationAddress);
    }

    [Fact]
    public void ASecondDestinationIsRefused()
    {
        // The library takes its fee contribution from the first output that is not the payee. With
        // a second payee that output is someone else's payment instead of this wallet's change.
        var model = CreateModel();
        model.Outputs.Add(new WalletSendModel.TransactionOutput
        {
            DestinationAddress = "tb1qsomeoneelse",
            Amount = 0.002m
        });

        Assert.Null(UIPayjoinSenderController.ResolveSingleDestination(model, out var error));
        Assert.Contains("one destination", error, System.StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyDestinationRowsAreIgnored()
    {
        // The send screen keeps a blank row after the operator removes a destination.
        var model = CreateModel();
        model.Outputs.Add(new WalletSendModel.TransactionOutput { DestinationAddress = "  " });

        Assert.NotNull(UIPayjoinSenderController.ResolveSingleDestination(model, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void SubtractingTheFeeFromTheAmountIsRefused()
    {
        // The receiver validates the amount it asked for, so the sender cannot pay less.
        var model = CreateModel();
        model.Outputs[0].SubtractFeesFromOutput = true;

        Assert.Null(UIPayjoinSenderController.ResolveSingleDestination(model, out var error));
        Assert.Contains("subtract the fee", error, System.StringComparison.Ordinal);
    }

    private static WalletSendModel CreateModel(string? payJoinBip21 = PayjoinUri)
    {
        return new WalletSendModel
        {
            PayJoinBIP21 = payJoinBip21!,
            Outputs =
            [
                new WalletSendModel.TransactionOutput
                {
                    DestinationAddress = "tb1qdestination",
                    Amount = 0.001m
                }
            ]
        };
    }
}
