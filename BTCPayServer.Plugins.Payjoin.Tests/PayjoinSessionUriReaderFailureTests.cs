using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class PayjoinSessionUriReaderFailureTests
{
    [Fact]
    public void DatabaseFailureRendersPlainBip21RatherThanThrowing()
    {
        using var testContext = new RelationalPluginTestContext();
        var reader = new PayjoinSessionUriReader(testContext.CreateStore());
        testContext.BreakDatabase();

        Assert.Null(reader.TryGetExistingPayjoinUri("invoice-broken-db", TestSessionStates.DefaultReceiverAddress));
    }

    [Fact]
    public void DatabaseFailureLeavesTheCheckoutModelUsable()
    {
        using var key = new Key();
        var address = key.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest).ToString();
        var plainUrl = $"bitcoin:{address}?amount=0.10000000";
        var plainUrlQr = $"bitcoin:{address.ToUpperInvariant()}?amount=0.10000000";
        using var testContext = new RelationalPluginTestContext();
        var logger = new CapturingLogger();
        var reader = new PayjoinSessionUriReader(testContext.CreateStore(), logger);
        testContext.BreakDatabase();
        var model = new BTCPayServer.Models.InvoicingModels.CheckoutModel
        {
            InvoiceBitcoinUrl = plainUrl,
            InvoiceBitcoinUrlQR = plainUrlQr
        };

        PayjoinBitcoinCheckoutModelExtension.ApplyPayjoinCheckoutModel(
            model, true, "/plugins/payjoin/invoices/x/payment-url", "invoice-broken-db",
            address, 0.1m, Network.RegTest, reader);

        var failure = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, failure.Level);
        Assert.Equal(1, failure.EventId.Id);
        var exception = Assert.IsType<SqliteException>(failure.Exception);
        Assert.Equal(1, exception.SqliteErrorCode);

        Assert.Equal(plainUrl, model.InvoiceBitcoinUrl);
        Assert.Equal(plainUrlQr, model.InvoiceBitcoinUrlQR);
        Assert.False(model.AdditionalData.ContainsKey(PayjoinBitcoinCheckoutModelExtension.PayjoinBitcoinUrlKey));
        Assert.False(model.AdditionalData.ContainsKey(PayjoinBitcoinCheckoutModelExtension.PayjoinBitcoinUrlQrKey));
        Assert.Equal(
            plainUrl,
            model.AdditionalData[PayjoinBitcoinCheckoutModelExtension.PlainBitcoinUrlKey].ToObject<string>());
        Assert.True(model.AdditionalData[PayjoinBitcoinCheckoutModelExtension.CanBootstrapKey].ToObject<bool>());
    }

    private sealed class CapturingLogger : ILogger<PayjoinSessionUriReader>
    {
        public List<(LogLevel Level, EventId EventId, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, eventId, exception));
        }
    }
}
