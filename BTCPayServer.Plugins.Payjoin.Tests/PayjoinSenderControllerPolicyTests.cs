using BTCPayServer.Plugins.Payjoin.Controllers;
using BTCPayServer.Plugins.Wallets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

/// <summary>
/// ASP.NET Core combines class-level and action-level authorization with AND: an action policy
/// adds to the class policy rather than replacing it. These tests pin the effective policy set
/// of each action, so a class-level policy cannot silently raise what an action requires.
/// </summary>
public class PayjoinSenderControllerPolicyTests
{
    [Fact]
    public void MvcCanBuildTheControllerActivator()
    {
        Assert.NotNull(ActivatorUtilities.CreateFactory(typeof(UIPayjoinSenderController), Type.EmptyTypes));
    }

    [Fact]
    public void TheControllerClassRequiresNoPolicy()
    {
        var attribute = Assert.Single(typeof(UIPayjoinSenderController).GetCustomAttributes<AuthorizeAttribute>(inherit: false));
        Assert.Null(attribute.Policy);
    }

    [Fact]
    public void SendingAuthorizesTheAutomaticSigningAndBroadcast()
    {
        Assert.Equal([WalletPolicies.CanCreateWalletTransactions, WalletPolicies.CanSignWalletTransactions,
            WalletPolicies.CanBroadcastWalletTransactions], EffectivePolicies(nameof(UIPayjoinSenderController.SendFromWallet)));
    }

    [Fact]
    public void CancelAndPayNowRequireTheirOwnPermissions()
    {
        Assert.Equal([WalletPolicies.CanCancelWalletTransactions], EffectivePolicies(nameof(UIPayjoinSenderController.Cancel)));
        Assert.Equal([WalletPolicies.CanBroadcastWalletTransactions], EffectivePolicies(nameof(UIPayjoinSenderController.PayNow)));
    }

    [Fact]
    public void TheSessionsPageNeedsOnlyWalletReadAccess()
    {
        Assert.Equal([WalletPolicies.CanViewWallet], EffectivePolicies(nameof(UIPayjoinSenderController.Send)));
    }

    private static string[] EffectivePolicies(string actionName)
    {
        var action = typeof(UIPayjoinSenderController).GetMethod(actionName);
        Assert.NotNull(action);
        var classPolicies = typeof(UIPayjoinSenderController)
            .GetCustomAttributes<AuthorizeAttribute>(inherit: false)
            .Select(x => x.Policy)
            .OfType<string>();
        var actionPolicies = action!
            .GetCustomAttributes<AuthorizeAttribute>(inherit: false)
            .Select(x => x.Policy)
            .OfType<string>();
        return classPolicies.Concat(actionPolicies).ToArray();
    }
}
