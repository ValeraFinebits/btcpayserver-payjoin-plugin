using BTCPayServer.Abstractions.Models;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBXplorer.Models;
using Xunit;
using InvoiceExceptionStatus = BTCPayServer.Client.Models.InvoiceExceptionStatus;
using InvoiceStatus = BTCPayServer.Client.Models.InvoiceStatus;
using SpeedPolicy = BTCPayServer.Client.Models.SpeedPolicy;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests;

[Collection(nameof(NonParallelizableCollectionDefinition))]
public class PayjoinAccountingUpgradeIntegrationTests : UnitTestBase
{
    public PayjoinAccountingUpgradeIntegrationTests(ITestOutputHelper helper) : base(helper)
    {
    }

    [Fact]
    [Trait("Integration", "Integration")]
    public async Task LegacyBridgeSurvivesMigrationAndReconcilesWithoutDerivationMetadata()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = cts.Token;
        var database = CreateDBTester();
        await database.MigrateAsync().WaitAsync(token).ConfigureAwait(true);
        try
        {
            var pluginFactory = new PayjoinPluginDbContextFactory(Options.Create(new DatabaseOptions
            {
                ConnectionString = database.ConnectionString
            }));
            var networkProvider = CreateNetworkProvider();
            var network = networkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode);
            var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(PayjoinConstants.BitcoinCode);
            var handler = new BitcoinLikePaymentHandler(paymentMethodId, null!, network, null!, null!, null!, null!, null!);
            var handlers = new PaymentMethodHandlerDictionary([handler]);
            using var events = new EventAggregator(BTCPayLogs);
            var invoiceRepository = new InvoiceRepository(database.CreateContextFactory(), events);
            var paymentService = new PaymentService(events, database.CreateContextFactory(), handlers, invoiceRepository);

            const long invoiceValueSats = 50_000;
            const long finalValueSats = 70_000;
            using var invoiceKey = new Key();
            using var settlementKey = new Key();
            var invoice = invoiceRepository.CreateNewInvoice("legacy-store");
            invoice.Currency = PayjoinConstants.BitcoinCode;
            invoice.Price = Money.Satoshis(invoiceValueSats).ToDecimal(MoneyUnit.BTC);
            invoice.SpeedPolicy = SpeedPolicy.MediumSpeed;
            invoice.SetPaymentPrompt(paymentMethodId, new PaymentPrompt
            {
                Currency = PayjoinConstants.BitcoinCode,
                Divisibility = 8,
                Destination = invoiceKey.PubKey.WitHash.GetAddress(Network.RegTest).ToString()
            });
            await using (var core = database.CreateContext())
            {
                core.Stores.Add(new StoreData { Id = invoice.StoreId, StoreName = "Legacy store" });
                var row = new InvoiceData
                {
                    Id = invoice.Id,
                    StoreDataId = invoice.StoreId,
                    Status = InvoiceStatus.New.ToString(),
                    ExceptionStatus = InvoiceExceptionStatus.None.ToString()
                };
                row.SetBlob(invoice);
                core.Invoices.Add(row);
                await core.SaveChangesAsync(token).ConfigureAwait(true);
            }

            var fallback = new OutPoint(uint256.One, 0);
            var fallbackPayment = new PaymentData
            {
                Id = fallback.ToString(),
                Created = DateTimeOffset.UtcNow,
                Status = PaymentStatus.Processing,
                Currency = PayjoinConstants.BitcoinCode,
                Amount = invoice.Price
            }.Set(invoice, handler, new BitcoinLikePaymentData { Outpoint = fallback, ConfirmationCount = 0 });
            Assert.NotNull(await paymentService.AddPayment(fallbackPayment).ConfigureAwait(true));

            var finalTransaction = Network.RegTest.CreateTransaction();
            finalTransaction.Inputs.Add(new OutPoint(uint256.One, 1));
            finalTransaction.Outputs.Add(Money.Satoshis(finalValueSats), settlementKey.PubKey.WitHash.ScriptPubKey);
            var finalTransactionId = finalTransaction.GetHash().ToString();
            var scriptHex = Convert.ToHexString(settlementKey.PubKey.WitHash.ScriptPubKey.ToBytes());
            var now = DateTimeOffset.UtcNow;
            await using (var plugin = pluginFactory.CreateContext())
            {
                await plugin.GetService<IMigrator>()
                    .MigrateAsync("20260707140242_RemoveReceiverSessionRelayUrl", token).ConfigureAwait(true);
                await plugin.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "BTCPayServer.Plugins.Payjoin"."AccountingBridges"
                        ("InvoiceId", "StoreId", "CryptoCode", "PaymentMethodId", "FallbackTransactionId",
                         "FallbackOutputIndex", "FallbackValueSats", "EffectiveInvoiceValueSats", "SettlementScript",
                         "ExpectedFinalTransactionId", "ExpectedFinalOutputIndex", "ExpectedFinalValueSats",
                         "Status", "CreatedAt", "UpdatedAt", "ExpiresAt")
                    VALUES ({invoice.Id}, {invoice.StoreId}, {PayjoinConstants.BitcoinCode}, {paymentMethodId.ToString()},
                            {fallback.Hash.ToString()}, {0L}, {invoiceValueSats}, {invoiceValueSats}, {scriptHex},
                            {finalTransactionId}, {0L}, {finalValueSats}, {1}, {now}, {now}, {now.AddHours(1)})
                    """, token).ConfigureAwait(true);
                await plugin.Database.MigrateAsync(token).ConfigureAwait(true);
                var upgraded = await plugin.AccountingBridges.SingleAsync(token).ConfigureAwait(true);
                Assert.Null(upgraded.SettlementKeyPath);
                Assert.Equal(PayjoinAccountingBridgeStatus.PendingFinalTransaction, upgraded.Status);
            }

            var detector = new PostgresPayjoinUniqueConstraintViolationDetector();
            var bridgeService = new PayjoinAccountingBridgeService(pluginFactory, detector, new PayjoinSessionBuildLock());
            var reader = new ObservedTransactionReader(finalTransaction);
            var accounting = new PayjoinAccountingPaymentService(
                new PayjoinInvoiceLookup(invoiceRepository),
                new PayjoinStalePaidOverCorrectionService(invoiceRepository),
                new PayjoinPlatformPaymentRecorder(paymentService),
                events, handlers, networkProvider, reader, new NoOpTransactionLabeler(),
                NullLogger<PayjoinAccountingPaymentService>.Instance);
            using var poller = new PayjoinReceiverPoller(
                new PayjoinReceiverSessionStore(pluginFactory, detector), new NoOpSessionProcessor(),
                bridgeService, accounting, NullLogger<PayjoinReceiverPoller>.Instance);

            await poller.ProcessTickOnceAsync(token).ConfigureAwait(true);
            var pending = await bridgeService.TryGetByInvoiceIdAsync(invoice.Id, token).ConfigureAwait(true);
            Assert.Equal(PayjoinAccountingBridgeStatus.PendingFinalTransaction, pending!.Status);
            Assert.Single((await invoiceRepository.GetInvoice(invoice.Id).ConfigureAwait(true)).GetPayments(false));

            reader.Confirmations = 1;
            await poller.ProcessTickOnceAsync(token).ConfigureAwait(true);
            await poller.ProcessTickOnceAsync(token).ConfigureAwait(true);
            var reconciled = await bridgeService.TryGetByInvoiceIdAsync(invoice.Id, token).ConfigureAwait(true);
            Assert.Equal(PayjoinAccountingBridgeStatus.Reconciled, reconciled!.Status);
            Assert.Null(reconciled.FailureMessage);
            var updatedInvoice = await invoiceRepository.GetInvoice(invoice.Id).ConfigureAwait(true);
            Assert.Equal(2, updatedInvoice.GetPayments(false).Count);
            var finalPayment = Assert.Single(updatedInvoice.GetPayments(true));
            Assert.Equal(new OutPoint(finalTransaction.GetHash(), 0).ToString(), finalPayment.Id);
            Assert.Equal(invoice.Price, finalPayment.Value);
            Assert.Equal(PaymentStatus.Settled, finalPayment.Status);
            Assert.Equal(settlementKey.PubKey.WitHash.GetAddress(Network.RegTest).ToString(), finalPayment.Destination);
            Assert.Equal(PaymentStatus.Unaccounted, updatedInvoice.GetPayments(false).Single(p => p.Id == fallback.ToString()).Status);
            var details = handler.ParsePaymentDetails(finalPayment.Details);
            Assert.Null(details.KeyPath);
            Assert.Null(details.KeyIndex);
        }
        finally
        {
            await using var core = database.CreateContext();
            await core.Database.EnsureDeletedAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    private sealed class ObservedTransactionReader(Transaction transaction) : IPayjoinWalletTransactionReader
    {
        public long Confirmations { get; set; }

        public Task<TransactionResult?> GetTransactionAsync(BTCPayNetwork network, uint256 transactionId, CancellationToken cancellationToken)
        {
            return Task.FromResult<TransactionResult?>(new TransactionResult
            {
                Transaction = transaction,
                TransactionHash = transaction.GetHash(),
                Confirmations = Confirmations
            });
        }
    }

    private sealed class NoOpSessionProcessor : IPayjoinReceiverSessionProcessor
    {
        public Task ProcessTickAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }

    private sealed class NoOpTransactionLabeler : IPayjoinTransactionLabeler
    {
        public Task LabelAsyncPayjoinAsync(WalletId walletId, uint256 transactionId, string invoiceId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
