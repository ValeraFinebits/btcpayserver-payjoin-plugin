using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Tests;
using NBitcoin;
using NBitpayClient;
using Xunit;
using Xunit.Abstractions;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class PayjoinPluginIntegrationTests : UnitTestBase
{
    public PayjoinPluginIntegrationTests(ITestOutputHelper helper) : base(helper)
    {
    }

    [Fact
    (Skip = "Manual Docker-backed integration test. Remove Skip to run it explicitly.")
    ]
    [Trait("Integration", "Integration")]
    public async Task CreateInvoiceAndPayItThroughThePayjoinPluginWithExternalPayer()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);
        var payer = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(tester, context.Network, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.User.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var paymentResult = await PayjoinIntegrationTestSupport.CreateAndPayInvoiceViaExternalPayjoinPayerAsync(tester, context.User, payer, context.Network, cts.Token).ConfigureAwait(true);

        PayjoinIntegrationTestSupport.AssertSuccessfulPayjoinTransaction(paymentResult);
    }

    [Fact
    (Skip = "Manual Docker-backed integration test. Remove Skip to run it explicitly.")
    ]
    [Trait("Integration", "Integration")]
    public async Task ExternalPayerSucceedsWhenReceiverProposalIsReplayedAcrossPollerTicks()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);
        var payer = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(tester, context.Network, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.User.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var (invoiceId, bip21Response) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.User, cts.Token).ConfigureAwait(true);
        PayjoinIntegrationTestSupport.AssertPayjoinBip21(bip21Response);

        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);

        var delay = TimeSpan.FromSeconds(5);
        var paymentTask = PayjoinIntegrationTestSupport.PayInvoiceViaExternalPayjoinPayerAsync(
            tester,
            payer,
            context.Network,
            context.User.StoreId,
            new Uri(bip21Response.Bip21, UriKind.Absolute),
            delay,
            cts.Token);

        await Task.Delay(delay, cts.Token).ConfigureAwait(true);

        var session = PayjoinReceiverTestHelper.GetRequiredReceiverSession(tester, invoiceId);
        Assert.True(session.TryGetContributedInput(out _));

        var transactionId = await paymentTask.ConfigureAwait(true);
        Assert.False(string.IsNullOrWhiteSpace(transactionId));

        await context.User.WaitInvoicePaid(invoiceId).WaitAsync(cts.Token).ConfigureAwait(true);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyRemovedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);
    }

    [Fact
    (Skip = "Manual Docker-backed integration test. Remove Skip to run it explicitly.")
    ]
    [Trait("Integration", "Integration")]
    public async Task SuccessfulPayjoinRemovesReceiverSession()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);
        var payer = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(tester, context.Network, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.User.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var (invoiceId, bip21Response) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.User, cts.Token).ConfigureAwait(true);
        PayjoinIntegrationTestSupport.AssertPayjoinBip21(bip21Response);

        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.PayInvoiceViaExternalPayjoinPayerAsync(
            tester,
            payer,
            context.Network,
            context.User.StoreId,
            new Uri(bip21Response.Bip21, UriKind.Absolute),
            cts.Token).ConfigureAwait(true);

        await context.User.WaitInvoicePaid(invoiceId).WaitAsync(cts.Token).ConfigureAwait(true);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyRemovedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);
    }

    [Fact
    (Skip = "Manual Docker-backed integration test. Remove Skip to run it explicitly.")
    ]
    [Trait("Integration", "Integration")]
    public async Task DisablePayjoinPreservesExistingStoreSettings()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        var coldDerivation = await PayjoinIntegrationTestSupport.CreateTrackedColdWalletAsync(tester, cts.Token).ConfigureAwait(true);
        var expectedDirectoryUrl = new Uri("https://directory.example.test/");
        var expectedRelayUrl = new Uri("https://relay.example.test/");

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.User.StoreId, settings =>
        {
            settings.ColdWalletDerivationScheme = coldDerivation.ToString();
            settings.DirectoryUrl = expectedDirectoryUrl;
            settings.OhttpRelayUrl = expectedRelayUrl;
        }, cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.DisablePayjoinAsync(tester, context.User.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var storeSettings = await tester.PayTester.GetService<IPayjoinStoreSettingsRepository>().GetAsync(context.User.StoreId).WaitAsync(cts.Token).ConfigureAwait(true);
        Assert.False(storeSettings.EnabledByDefault);
        Assert.Equal(coldDerivation.ToString(), storeSettings.ColdWalletDerivationScheme);
        Assert.Equal(expectedDirectoryUrl, storeSettings.DirectoryUrl);
        Assert.Equal(expectedRelayUrl, storeSettings.OhttpRelayUrl);
    }

    [Fact
    (Skip = "Manual Docker-backed integration test. Remove Skip to run it explicitly.")
    ]
    [Trait("Integration", "Integration")]
    public async Task GetBip21DoesNotEnablePayjoinWhenMerchantHasNoCoins()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        await tester.StartAsync().WaitAsync(cts.Token).ConfigureAwait(true);

        var network = tester.NetworkProvider.GetNetwork<BTCPayNetwork>("BTC");
        Assert.NotNull(network);

        var merchant = tester.NewAccount();
        await merchant.GrantAccessAsync().WaitAsync(cts.Token).ConfigureAwait(true);
        await merchant.RegisterDerivationSchemeAsync("BTC", ScriptPubKeyType.Segwit, true).WaitAsync(cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, merchant.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var confirmedOutpoints = await PayjoinIntegrationTestSupport.GetReceiverOutpointsAsync(tester, merchant.StoreId, confirmedOnly: true, cts.Token).ConfigureAwait(true);
        Assert.Empty(confirmedOutpoints);

        var (_, bip21Response) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, merchant, cts.Token).ConfigureAwait(true);

        PayjoinIntegrationTestSupport.AssertPlainBip21(bip21Response);
    }

    [Fact
    (Skip = "Manual Docker-backed integration test. Remove Skip to run it explicitly.")
    ]
    [Trait("Integration", "Integration")]
    public async Task PayjoinChangeOutputGoesToColdWalletWithExternalPayer()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);
        var payer = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(tester, context.Network, cancellationToken: cts.Token).ConfigureAwait(true);

        var coldDerivation = await PayjoinIntegrationTestSupport.CreateTrackedColdWalletAsync(tester, cts.Token).ConfigureAwait(true);
        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.User.StoreId, settings =>
        {
            settings.ColdWalletDerivationScheme = coldDerivation.ToString();
        }, cts.Token).ConfigureAwait(true);

        var paymentResult = await PayjoinIntegrationTestSupport.CreateAndPayInvoiceViaExternalPayjoinPayerAsync(tester, context.User, payer, context.Network, cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.AssertColdWalletReceivedPayjoinChangeAsync(tester, coldDerivation, paymentResult, cts.Token).ConfigureAwait(true);
    }

    [Fact
    (Skip = "Manual Docker-backed integration test. Remove Skip to run it explicitly.")
    ]
    [Trait("Integration", "Integration")]
    public async Task GetBip21DoesNotEnablePayjoinWhenMerchantHasOnlyUnconfirmedCoins()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, confirmFunding: false, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.User.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var confirmedOutpoints = await PayjoinIntegrationTestSupport.GetReceiverOutpointsAsync(tester, context.User.StoreId, confirmedOnly: true, cts.Token).ConfigureAwait(true);
        Assert.Empty(confirmedOutpoints);

        var allOutpoints = await PayjoinIntegrationTestSupport.GetReceiverOutpointsAsync(tester, context.User.StoreId, confirmedOnly: false, cts.Token).ConfigureAwait(true);
        Assert.NotEmpty(allOutpoints);

        var (_, bip21Response) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.User, cts.Token).ConfigureAwait(true);

        PayjoinIntegrationTestSupport.AssertPlainBip21(bip21Response);
    }

    [Fact
    (Skip = "Manual Docker-backed integration test. Remove Skip to run it explicitly.")
    ]
    [Trait("Integration", "Integration")]
    public async Task GetBip21DoesNotEnablePayjoinWhenStoreSettingDisabledDoesNotCreateReceiverSession()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.DisablePayjoinAsync(tester, context.User.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var (invoiceId, bip21Response) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.User, cts.Token).ConfigureAwait(true);

        PayjoinIntegrationTestSupport.AssertPlainBip21(bip21Response);

        var sessionStore = tester.PayTester.GetService<PayjoinReceiverSessionStore>();
        Assert.False(sessionStore.TryGetSession(invoiceId, out _));
        Assert.DoesNotContain(sessionStore.GetSessions(), session => session.InvoiceId == invoiceId);
    }

    [Fact
    (Skip = "Manual Docker-backed integration test. Remove Skip to run it explicitly.")
    ]
    [Trait("Integration", "Integration")]
    public async Task CheckoutModelUsesPayjoinUrlAndCreatesReceiverSessionWhenEnabled()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.User.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var invoice = await context.User.BitPay.CreateInvoiceAsync(new Invoice
        {
            Price = 0.1m,
            Currency = "BTC",
            FullNotifications = true
        }).WaitAsync(cts.Token).ConfigureAwait(true);

        var checkoutModel = await PayjoinIntegrationTestSupport.GetCheckoutModelAsync(tester, invoice.Id, cts.Token).ConfigureAwait(true);

        Assert.Contains("pj=", checkoutModel.InvoiceBitcoinUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pjos=0", checkoutModel.InvoiceBitcoinUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pj=", checkoutModel.InvoiceBitcoinUrlQR, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pjos=0", checkoutModel.InvoiceBitcoinUrlQR, StringComparison.OrdinalIgnoreCase);
        Assert.True(checkoutModel.AdditionalData.ContainsKey(PayjoinBitcoinCheckoutModelExtension.PlainBitcoinUrlKey));
        Assert.True(checkoutModel.AdditionalData.ContainsKey(PayjoinBitcoinCheckoutModelExtension.PayjoinBitcoinUrlKey));

        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(tester, invoice.Id, cts.Token).ConfigureAwait(true);
    }

    [Fact
    (Skip = "Manual Docker-backed integration test. Remove Skip to run it explicitly.")
    ]
    [Trait("Integration", "Integration")]
    public async Task CheckoutModelFallsBackToPlainBip21WhenDisabled()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.DisablePayjoinAsync(tester, context.User.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var invoice = await context.User.BitPay.CreateInvoiceAsync(new Invoice
        {
            Price = 0.1m,
            Currency = "BTC",
            FullNotifications = true
        }).WaitAsync(cts.Token).ConfigureAwait(true);

        var checkoutModel = await PayjoinIntegrationTestSupport.GetCheckoutModelAsync(tester, invoice.Id, cts.Token).ConfigureAwait(true);

        Assert.DoesNotContain("pj=", checkoutModel.InvoiceBitcoinUrl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pjos=", checkoutModel.InvoiceBitcoinUrl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pj=", checkoutModel.InvoiceBitcoinUrlQR, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pjos=", checkoutModel.InvoiceBitcoinUrlQR, StringComparison.OrdinalIgnoreCase);
        Assert.False(checkoutModel.AdditionalData.ContainsKey(PayjoinBitcoinCheckoutModelExtension.PlainBitcoinUrlKey));
        Assert.False(checkoutModel.AdditionalData.ContainsKey(PayjoinBitcoinCheckoutModelExtension.PayjoinBitcoinUrlKey));

        var sessionStore = tester.PayTester.GetService<PayjoinReceiverSessionStore>();
        Assert.False(sessionStore.TryGetSession(invoice.Id, out _));
    }

    [Fact
    (Skip = "Manual Docker-backed integration test. Remove Skip to run it explicitly.")
    ]
    [Trait("Integration", "Integration")]
    public async Task GetBip21IsIdempotentForSameInvoice()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.User.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var (invoiceId, firstResponse) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.User, cts.Token).ConfigureAwait(true);
        PayjoinIntegrationTestSupport.AssertPayjoinBip21(firstResponse);

        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);

        var sessionStore = tester.PayTester.GetService<PayjoinReceiverSessionStore>();
        Assert.Single(sessionStore.GetSessions(), s => s.InvoiceId == invoiceId);

        var secondResponse = await PayjoinIntegrationTestSupport.GetBip21Async(tester, invoiceId, cts.Token).ConfigureAwait(true);

        PayjoinIntegrationTestSupport.AssertPayjoinBip21(secondResponse);
        Assert.Equal(firstResponse.Bip21, secondResponse.Bip21);
        Assert.Single(sessionStore.GetSessions(), s => s.InvoiceId == invoiceId);
    }

    [Fact
    (Skip = "Manual Docker-backed integration test. Remove Skip to run it explicitly.")
    ]
    [Trait("Integration", "Integration")]
    public async Task GetBip21DoesNotEnablePayjoinWhenStoreSettingDisabled()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.DisablePayjoinAsync(tester, context.User.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var (_, bip21Response) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.User, cts.Token).ConfigureAwait(true);

        PayjoinIntegrationTestSupport.AssertPlainBip21(bip21Response);
    }

    [Fact
    (Skip = "Manual Docker-backed integration test. Remove Skip to run it explicitly.")
    ]
    [Trait("Integration", "Integration")]
    public async Task GetBip21DoesNotEnablePayjoinWhenOhttpRelayUrlMissing()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.User.StoreId, settings =>
        {
            settings.OhttpRelayUrl = null;
        }, cts.Token).ConfigureAwait(true);

        var (_, bip21Response) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.User, cts.Token).ConfigureAwait(true);

        PayjoinIntegrationTestSupport.AssertPlainBip21(bip21Response);
    }

    [Fact
    (Skip = "Manual Docker-backed integration test. Remove Skip to run it explicitly.")
    ]
    [Trait("Integration", "Integration")]
    public async Task GetBip21DoesNotEnablePayjoinWhenDirectoryUrlMissing()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.User.StoreId, settings =>
        {
            settings.DirectoryUrl = null;
        }, cts.Token).ConfigureAwait(true);

        var (_, bip21Response) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.User, cts.Token).ConfigureAwait(true);

        PayjoinIntegrationTestSupport.AssertPlainBip21(bip21Response);
    }

    [Fact
    (Skip = "Manual Docker-backed integration test. Remove Skip to run it explicitly.")
    ]
    [Trait("Integration", "Integration")]
    public async Task GetBip21FallsBackToPlainBip21WhenOhttpKeysFetchFails()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.User.StoreId, settings =>
        {
            settings.OhttpRelayUrl = new Uri("http://127.0.0.1:1/");
        }, cts.Token).ConfigureAwait(true);

        var (_, bip21Response) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.User, cts.Token).ConfigureAwait(true);

        PayjoinIntegrationTestSupport.AssertPlainBip21(bip21Response);
    }

    [Fact
    (Skip = "Manual Docker-backed integration test. Remove Skip to run it explicitly.")
    ]
    [Trait("Integration", "Integration")]
    public async Task ReceiverSessionIsRemovedWhenInvoiceGetsPaidWithoutPayjoin()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.User.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var (invoiceId, bip21Response) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.User, cts.Token).ConfigureAwait(true);
        PayjoinIntegrationTestSupport.AssertPayjoinBip21(bip21Response);

        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);

        await context.User.PayInvoice(invoiceId).WaitAsync(cts.Token).ConfigureAwait(true);

        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyRemovedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);
    }

    [Fact
    (Skip = "Manual Docker-backed integration test. Remove Skip to run it explicitly.")
    ]
    [Trait("Integration", "Integration")]
    public async Task ReceiverSessionIsRemovedWhenInvoiceExpires()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.User.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var (invoiceId, bip21Response) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.User, cts.Token).ConfigureAwait(true);
        PayjoinIntegrationTestSupport.AssertPayjoinBip21(bip21Response);

        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);

        var invoiceRepository = tester.PayTester.GetService<InvoiceRepository>();
        await invoiceRepository.UpdateInvoiceExpiry(invoiceId, TimeSpan.Zero).WaitAsync(cts.Token).ConfigureAwait(true);

        await PayjoinInvoiceTestHelper.AssertInvoiceStatusEventuallyAsync(tester, invoiceId, InvoiceStatus.Expired, cts.Token).ConfigureAwait(true);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyRemovedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);
    }

    [Fact
    (Skip = "Manual Docker-backed integration test. Remove Skip to run it explicitly.")
    ]
    [Trait("Integration", "Integration")]
    public async Task ReceiverSessionIsRemovedWhenInvoiceBecomesInvalid()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.User.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var (invoiceId, bip21Response) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.User, cts.Token).ConfigureAwait(true);
        PayjoinIntegrationTestSupport.AssertPayjoinBip21(bip21Response);

        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);

        var invoiceRepository = tester.PayTester.GetService<InvoiceRepository>();
        var marked = await invoiceRepository.MarkInvoiceStatus(invoiceId, InvoiceStatus.Invalid).WaitAsync(cts.Token).ConfigureAwait(true);
        Assert.True(marked);

        await PayjoinInvoiceTestHelper.AssertInvoiceStatusEventuallyAsync(tester, invoiceId, InvoiceStatus.Invalid, cts.Token).ConfigureAwait(true);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyRemovedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);
    }

    [Fact
    (Skip = "Manual Docker-backed integration test. Remove Skip to run it explicitly.")
    ]
    [Trait("Integration", "Integration")]
    public async Task PayjoinRequestFailsWhenInvoiceWasAlreadyPaid()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);
        var payer = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(tester, context.Network, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.User.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var (invoiceId, bip21Response) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.User, cts.Token).ConfigureAwait(true);
        PayjoinIntegrationTestSupport.AssertPayjoinBip21(bip21Response);

        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);

        await context.User.PayInvoice(invoiceId).WaitAsync(cts.Token).ConfigureAwait(true);
        await context.User.WaitInvoicePaid(invoiceId).WaitAsync(cts.Token).ConfigureAwait(true);

        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCloseRequestedOrRemovedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PayjoinIntegrationTestSupport.PayInvoiceViaExternalPayjoinPayerAsync(
                tester,
                payer,
                context.Network,
                context.User.StoreId,
                new Uri(bip21Response.Bip21, UriKind.Absolute),
                cts.Token)).ConfigureAwait(true);

        Assert.Contains("rejected", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("timed out", exception.Message, StringComparison.OrdinalIgnoreCase);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyRemovedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);
    }

    [Fact
    (Skip = "Manual Docker-backed integration test. Remove Skip to run it explicitly.")
    ]
    [Trait("Integration", "Integration")]
    public async Task PayjoinRequestFailsWhenInvoiceWasExpired()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);
        var payer = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(tester, context.Network, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.User.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var (invoiceId, bip21Response) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.User, cts.Token).ConfigureAwait(true);
        PayjoinIntegrationTestSupport.AssertPayjoinBip21(bip21Response);

        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);

        var invoiceRepository = tester.PayTester.GetService<InvoiceRepository>();
        await invoiceRepository.UpdateInvoiceExpiry(invoiceId, TimeSpan.Zero).WaitAsync(cts.Token).ConfigureAwait(true);

        await PayjoinInvoiceTestHelper.AssertInvoiceStatusEventuallyAsync(tester, invoiceId, InvoiceStatus.Expired, cts.Token).ConfigureAwait(true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PayjoinIntegrationTestSupport.PayInvoiceViaExternalPayjoinPayerAsync(
                tester,
                payer,
                context.Network,
                context.User.StoreId,
                new Uri(bip21Response.Bip21, UriKind.Absolute),
                cts.Token)).ConfigureAwait(true);

        Assert.Contains("rejected", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("timed out", exception.Message, StringComparison.OrdinalIgnoreCase);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyRemovedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);
    }

    [Fact
    (Skip = "Manual Docker-backed integration test. Remove Skip to run it explicitly.")
    ]
    [Trait("Integration", "Integration")]
    public async Task PayjoinRequestFailsWhenInvoiceBecomesInvalid()
    {
        using var cts = new CancellationTokenSource(PayjoinIntegrationTestSupport.TestTimeout);
        using var tester = CreateServerTester(newDb: true);
        var context = await PayjoinAccountTestHelper.CreateInitializedTestContextAsync(tester, cancellationToken: cts.Token).ConfigureAwait(true);
        var payer = await PayjoinAccountTestHelper.CreateInitializedAccountAsync(tester, context.Network, cancellationToken: cts.Token).ConfigureAwait(true);

        await PayjoinIntegrationTestSupport.EnablePayjoinAsync(tester, context.User.StoreId, cancellationToken: cts.Token).ConfigureAwait(true);

        var (invoiceId, bip21Response) = await PayjoinIntegrationTestSupport.CreateInvoiceAndGetBip21Async(tester, context.User, cts.Token).ConfigureAwait(true);
        PayjoinIntegrationTestSupport.AssertPayjoinBip21(bip21Response);

        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCreatedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);

        var invoiceRepository = tester.PayTester.GetService<InvoiceRepository>();
        var marked = await invoiceRepository.MarkInvoiceStatus(invoiceId, InvoiceStatus.Invalid).WaitAsync(cts.Token).ConfigureAwait(true);
        Assert.True(marked);

        await PayjoinInvoiceTestHelper.AssertInvoiceStatusEventuallyAsync(tester, invoiceId, InvoiceStatus.Invalid, cts.Token).ConfigureAwait(true);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyCloseRequestedOrRemovedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PayjoinIntegrationTestSupport.PayInvoiceViaExternalPayjoinPayerAsync(
                tester,
                payer,
                context.Network,
                context.User.StoreId,
                new Uri(bip21Response.Bip21, UriKind.Absolute),
                cts.Token)).ConfigureAwait(true);

        Assert.Contains("rejected", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("timed out", exception.Message, StringComparison.OrdinalIgnoreCase);
        await PayjoinReceiverTestHelper.AssertReceiverSessionEventuallyRemovedAsync(tester, invoiceId, cts.Token).ConfigureAwait(true);
    }

}
