using BTCPayServer.Plugins.Payjoin.Services;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class PayjoinReceiverSessionStateTests
{
    [Fact]
    public void ContributedInputsAreEmptyByDefault()
    {
        var session = CreateSession();

        Assert.Empty(session.GetContributedInputs());
    }

    [Fact]
    public void ContributedInputsRoundTripWithDefensiveCopies()
    {
        var session = CreateSession();
        var contributedInput = new PayjoinReceiverContributedInput(
            new OutPoint(uint256.Parse("1111111111111111111111111111111111111111111111111111111111111111"), 1),
            new KeyPath("0/1"));

        session.SetContributedInputs(contributedInput);

        var firstRead = session.GetContributedInputs();
        Assert.Single(firstRead);
        Assert.Equal(contributedInput.OutPoint, firstRead[0].OutPoint);
        Assert.Equal(contributedInput.KeyPath, firstRead[0].KeyPath);

        firstRead[0] = new PayjoinReceiverContributedInput(
            new OutPoint(uint256.Parse("2222222222222222222222222222222222222222222222222222222222222222"), 2),
            new KeyPath("1/2"));

        var secondRead = session.GetContributedInputs();
        Assert.Single(secondRead);
        Assert.Equal(contributedInput.OutPoint, secondRead[0].OutPoint);
        Assert.Equal(contributedInput.KeyPath, secondRead[0].KeyPath);
    }

    [Fact]
    public void SetContributedInputsCopiesInputArray()
    {
        var session = CreateSession();
        var originalInput = new PayjoinReceiverContributedInput(
            new OutPoint(uint256.Parse("4444444444444444444444444444444444444444444444444444444444444444"), 4),
            new KeyPath("3/4"));
        var replacementInput = new PayjoinReceiverContributedInput(
            new OutPoint(uint256.Parse("5555555555555555555555555555555555555555555555555555555555555555"), 5),
            new KeyPath("4/5"));
        var contributedInputs = new[] { originalInput };

        session.SetContributedInputs(contributedInputs);
        contributedInputs[0] = replacementInput;

        var storedInputs = session.GetContributedInputs();
        Assert.Single(storedInputs);
        Assert.Equal(originalInput.OutPoint, storedInputs[0].OutPoint);
        Assert.Equal(originalInput.KeyPath, storedInputs[0].KeyPath);
    }

    [Fact]
    public void MultipleContributedInputsRoundTripInOrder()
    {
        var session = CreateSession();
        var firstInput = new PayjoinReceiverContributedInput(
            new OutPoint(uint256.Parse("6666666666666666666666666666666666666666666666666666666666666666"), 6),
            new KeyPath("5/6"));
        var secondInput = new PayjoinReceiverContributedInput(
            new OutPoint(uint256.Parse("7777777777777777777777777777777777777777777777777777777777777777"), 7),
            new KeyPath("6/7"));

        session.SetContributedInputs(firstInput, secondInput);

        var storedInputs = session.GetContributedInputs();
        Assert.Equal(2, storedInputs.Length);
        Assert.Equal(firstInput.OutPoint, storedInputs[0].OutPoint);
        Assert.Equal(firstInput.KeyPath, storedInputs[0].KeyPath);
        Assert.Equal(secondInput.OutPoint, storedInputs[1].OutPoint);
        Assert.Equal(secondInput.KeyPath, storedInputs[1].KeyPath);
    }

    [Fact]
    public void ClearContributedInputsRemovesStoredMetadata()
    {
        var session = CreateSession();
        session.SetContributedInputs(new PayjoinReceiverContributedInput(
            new OutPoint(uint256.Parse("3333333333333333333333333333333333333333333333333333333333333333"), 3),
            new KeyPath("2/3")));

        session.ClearContributedInputs();

        Assert.Empty(session.GetContributedInputs());
    }

    private static PayjoinReceiverSessionState CreateSession()
    {
        return new PayjoinReceiverSessionState(
            "invoice-1",
            "store-1",
            "bcrt1qexampleaddress0000000000000000000000000",
            new Uri("https://relay.example/"),
            DateTimeOffset.UtcNow.AddMinutes(5),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
    }
}
