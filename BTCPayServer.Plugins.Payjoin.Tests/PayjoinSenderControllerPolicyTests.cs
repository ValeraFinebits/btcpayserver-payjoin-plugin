using BTCPayServer.Client;
using BTCPayServer.Plugins.Payjoin.Controllers;
using BTCPayServer.Plugins.Wallets;
using Microsoft.AspNetCore.Authorization;
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
    public void TheControllerClassRequiresNoPolicy()
    {
        var attribute = Assert.Single(typeof(UIPayjoinSenderController).GetCustomAttributes<AuthorizeAttribute>(inherit: false));
        Assert.Null(attribute.Policy);
    }

    [Fact]
    public void SendingFromTheWalletNeedsOnlyTheWalletPermission()
    {
        // A user who may create wallet transactions must be able to send an async payjoin, and
        // must not need the store-settings permission on top of it.
        Assert.Equal([WalletPolicies.CanCreateWalletTransactions], EffectivePolicies(nameof(UIPayjoinSenderController.SendFromWallet)));
    }

    [Fact]
    public void StoppingASessionNeedsOnlyTheWalletPermission()
    {
        // Stopping a payjoin broadcasts the plain payment, so it carries the same permission as
        // starting one.
        Assert.Equal([WalletPolicies.CanCreateWalletTransactions], EffectivePolicies(nameof(UIPayjoinSenderController.Cancel)));
    }

    [Fact]
    public void TheSessionsPageNeedsTheStoreSettingsPermission()
    {
        Assert.Equal([Policies.CanModifyStoreSettings], EffectivePolicies(nameof(UIPayjoinSenderController.Send)));
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
