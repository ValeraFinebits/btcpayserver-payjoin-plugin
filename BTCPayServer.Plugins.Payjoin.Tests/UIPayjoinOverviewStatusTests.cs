using Microsoft.Extensions.Localization;
using NBitcoin;
using NSubstitute;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class UIPayjoinOverviewStatusTests
{
    private static UIPayjoinOverviewController CreateController()
    {
        var localizer = Substitute.For<IStringLocalizer>();
        localizer[Arg.Any<string>()].Returns(callInfo => new LocalizedString((string)callInfo[0], (string)callInfo[0]));
        return new UIPayjoinOverviewController(null!, null!, null!, null!, null!, null!, null!, localizer);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PendingStatusNamesTheEffectiveFallback(bool v1FallbackEffective)
    {
        using var controller = CreateController();
        var status = controller.ResolveStatus(
            directoryConfigured: true,
            relayConfigured: true,
            networkAvailable: true,
            hasConfirmedReceiverInputs: false,
            v1FallbackEffective: v1FallbackEffective);

        Assert.Equal("warning", status.Severity);
        AssertNamesFallback(status.Message, v1FallbackEffective);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadyStatusNamesTheEffectiveFallback(bool v1FallbackEffective)
    {
        using var controller = CreateController();
        var status = controller.ResolveStatus(
            directoryConfigured: true,
            relayConfigured: true,
            networkAvailable: true,
            hasConfirmedReceiverInputs: true,
            v1FallbackEffective: v1FallbackEffective);

        Assert.Equal("success", status.Severity);
        AssertNamesFallback(status.Message, v1FallbackEffective);
    }

    private static void AssertNamesFallback(string message, bool v1FallbackEffective)
    {
        Assert.DoesNotContain("BIP21", message, StringComparison.Ordinal);
        if (v1FallbackEffective)
        {
            Assert.Contains("Payjoin v1 (BIP 78)", message, StringComparison.Ordinal);
            Assert.DoesNotContain("standard Bitcoin", message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("standard Bitcoin", message, StringComparison.Ordinal);
            Assert.DoesNotContain("Payjoin v1", message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false, true, true, "danger")]   // BTC network unavailable
    [InlineData(true, false, true, "danger")]   // directory URL missing
    [InlineData(true, true, false, "danger")]   // OHTTP relay URL missing
    public void SeverityFollowsTheMissingPrerequisite(bool networkAvailable, bool directoryConfigured, bool relayConfigured, string expectedSeverity)
    {
        using var controller = CreateController();
        var status = controller.ResolveStatus(
            directoryConfigured: directoryConfigured,
            relayConfigured: relayConfigured,
            networkAvailable: networkAvailable,
            hasConfirmedReceiverInputs: true,
            v1FallbackEffective: false);

        Assert.Equal(expectedSeverity, status.Severity);
    }

    [Theory]
    [InlineData(true, true, true, true, ScriptPubKeyType.Segwit, true)]
    [InlineData(true, true, true, true, ScriptPubKeyType.SegwitP2SH, true)]
    [InlineData(true, true, true, true, ScriptPubKeyType.TaprootBIP86, true)]
    [InlineData(true, true, true, true, ScriptPubKeyType.Legacy, false)]    // legacy scripts cannot payjoin
    [InlineData(true, true, true, false, ScriptPubKeyType.Segwit, false)]   // cold / watch-only wallet
    [InlineData(true, true, false, true, ScriptPubKeyType.Segwit, false)]   // pruned/old node can't check transactions
    [InlineData(false, true, true, true, ScriptPubKeyType.Segwit, false)]   // payjoin toggle disabled
    [InlineData(true, false, true, true, ScriptPubKeyType.Segwit, false)]   // network without payjoin support
    public void EffectiveV1GateMatchesCoreRule(bool payJoinEnabled, bool networkSupportsPayJoin, bool nodeSupportsTransactionCheck, bool isHotWallet, ScriptPubKeyType scriptType, bool expected)
    {
        Assert.Equal(expected, UIPayjoinOverviewController.IsPayjoinV1Effective(payJoinEnabled, networkSupportsPayJoin, nodeSupportsTransactionCheck, isHotWallet, scriptType));
    }

    [Theory]
    [InlineData(true, true, PayjoinCheckoutMode.AsyncPayjoin)]
    [InlineData(true, false, PayjoinCheckoutMode.AsyncPayjoin)]
    [InlineData(false, true, PayjoinCheckoutMode.PayjoinV1)]
    [InlineData(false, false, PayjoinCheckoutMode.StandardBitcoin)]
    public void DefaultCheckoutModeIsTheTopOfTheChain(bool payjoinV2Default, bool v1FallbackEffective, PayjoinCheckoutMode expected)
    {
        Assert.Equal(expected, UIPayjoinOverviewController.ResolveDefaultCheckoutMode(payjoinV2Default, v1FallbackEffective));
    }

    [Theory]
    [InlineData(true, true, PayjoinCheckoutMode.PayjoinV1)]        // Async Payjoin -> Payjoin v1
    [InlineData(true, false, PayjoinCheckoutMode.StandardBitcoin)] // Async Payjoin -> Standard Bitcoin (no v1)
    [InlineData(false, true, PayjoinCheckoutMode.StandardBitcoin)] // Payjoin v1 -> Standard Bitcoin
    public void FallbackTargetIsOneRungBelowTheDefault(bool payjoinV2Default, bool v1FallbackEffective, PayjoinCheckoutMode expected)
    {
        Assert.Equal(expected, UIPayjoinOverviewController.ResolveFallbackTarget(payjoinV2Default, v1FallbackEffective));
    }

    [Fact]
    public void FallbackTargetIsHiddenWhenTheDefaultIsAlreadyPlainBitcoin()
    {
        Assert.Null(UIPayjoinOverviewController.ResolveFallbackTarget(payjoinV2Default: false, v1FallbackEffective: false));
    }
}
