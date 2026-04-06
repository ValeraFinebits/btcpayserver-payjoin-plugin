using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Payjoin;
using System;
using System.Threading;
using System.Threading.Tasks;
using PayjoinUri = Payjoin.Uri;

namespace BTCPayServer.Plugins.Payjoin.Controllers;

[Route("~/plugins/payjoin")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanViewProfile)]
public class UIPayJoinController : Controller
{
    private const string BitcoinCode = "BTC";
    private static readonly Action<ILogger, string, string, Exception?> LogPayjoinSenderBroadcasted =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(1, nameof(LogPayjoinSenderBroadcasted)),
            "Payjoin sender broadcasted payjoin transaction {TransactionId} for {InvoiceId}");
    private static readonly Action<ILogger, string, string, Exception?> LogRunTestPaymentInvariantFailed =
        LoggerMessage.Define<string, string>(LogLevel.Warning, new EventId(2, nameof(LogRunTestPaymentInvariantFailed)),
            "RunTestPayment self-pay invariant failed for {InvoiceId}: {Message}");
    private static readonly Action<ILogger, string, string, Exception?> LogRunTestPaymentInvariantDiagnostics =
        LoggerMessage.Define<string, string>(LogLevel.Debug, new EventId(3, nameof(LogRunTestPaymentInvariantDiagnostics)),
            "RunTestPayment self-pay diagnostics for {InvoiceId}: {Diagnostics}");

    private readonly BTCPayServerEnvironment _env;
    private readonly InvoiceRepository _invoiceRepository;
    private readonly StoreRepository _storeRepository;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly IPayjoinStoreSettingsRepository _storeSettingsRepository;
    private readonly IRunTestPaymentService _runTestPaymentService;
    private readonly ILogger<UIPayJoinController>? _logger;

    public UIPayJoinController(
        BTCPayServerEnvironment env,
        InvoiceRepository invoiceRepository,
        StoreRepository storeRepository,
        PaymentMethodHandlerDictionary handlers,
        BTCPayNetworkProvider networkProvider,
        IPayjoinStoreSettingsRepository storeSettingsRepository,
        IRunTestPaymentService runTestPaymentService,
        ILogger<UIPayJoinController>? logger = null)
    {
        _env = env;
        _invoiceRepository = invoiceRepository;
        _storeRepository = storeRepository;
        _handlers = handlers;
        _networkProvider = networkProvider;
        _storeSettingsRepository = storeSettingsRepository;
        _runTestPaymentService = runTestPaymentService;
        _logger = logger;
    }

    // TODO: Remove this test endpoint.
    // This cheat-mode-only self-pay flow intentionally exercises the unusual case where
    // the sender and receiver paths operate against the same store wallet, so we can
    // catch same-wallet payjoin edge cases that do not show up with external payers.
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    [HttpPost("run-test-payment")]
    public async Task<ActionResult<RunTestPaymentResponse>> RunTestPayment([FromBody] RunTestPaymentRequest request, CancellationToken cancellationToken)
    {
        if (!_env.CheatMode)
        {
            return NotFound();
        }

        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.InvoiceId))
        {
            return RunTestPaymentFailure("invoiceId is required");
        }

        if (request.PaymentUrl is null)
        {
            return RunTestPaymentFailure("paymentUrl is required");
        }

        var invoice = await _invoiceRepository.GetInvoice(request.InvoiceId).ConfigureAwait(false);
        if (invoice is null)
        {
            return RunTestPaymentFailure("invoice not found");
        }

        var store = await _storeRepository.FindStore(invoice.StoreId).ConfigureAwait(false);
        if (store is null)
        {
            return RunTestPaymentFailure("store not found");
        }

        var storeSettings = await _storeSettingsRepository.GetAsync(invoice.StoreId).ConfigureAwait(false);
        if (storeSettings is null || storeSettings.OhttpRelayUrl is null)
        {
            return RunTestPaymentFailure("OhttpRelayUrl not found");
        }

        var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(BitcoinCode);
        var derivationScheme = store.GetPaymentMethodConfig<DerivationSchemeSettings>(paymentMethodId, _handlers, true);
        if (derivationScheme is null)
        {
            return RunTestPaymentFailure("wallet not configured");
        }

        var network = _networkProvider.GetNetwork<BTCPayNetwork>(BitcoinCode);
        if (network is null)
        {
            return RunTestPaymentFailure("network not available");
        }

        string paymentAddress;
        decimal paymentAmount;
        try
        {
            using var parsedUri = PayjoinUri.Parse(request.PaymentUrl.ToString());
            paymentAddress = parsedUri.Address();
            var amountSats = parsedUri.AmountSats();
            if (amountSats is null)
            {
                return RunTestPaymentFailure("payment amount missing in paymentUrl");
            }

            paymentAmount = Money.Satoshis(checked((long)amountSats.Value)).ToDecimal(MoneyUnit.BTC);
            using var _ = parsedUri.CheckPjSupported();
        }
        catch (PjParseException ex)
        {
            return RunTestPaymentFailure($"Invalid BIP21 URI: {ex.Message}");
        }
        catch (PjNotSupported ex)
        {
            return RunTestPaymentFailure($"Payjoin not available in URI: {ex.Message}");
        }

        BitcoinAddress paymentAddressValue;
        try
        {
            paymentAddressValue = BitcoinAddress.Create(paymentAddress, network.NBitcoinNetwork);
        }
        catch (FormatException ex)
        {
            return RunTestPaymentFailure($"Invalid payment address for network: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            return RunTestPaymentFailure($"Invalid payment address for network: {ex.Message}");
        }

        var runTestPaymentContext = new RunTestPaymentContext(
            request.InvoiceId,
            request.PaymentUrl,
            storeSettings.OhttpRelayUrl,
            paymentAddressValue,
            paymentAmount,
            network,
            derivationScheme);

        try
        {
            var txid = await _runTestPaymentService.ExecuteAsync(runTestPaymentContext, cancellationToken).ConfigureAwait(false);
            if (_logger is not null)
            {
                LogPayjoinSenderBroadcasted(_logger, txid, request.InvoiceId, null);
            }

            return RunTestPaymentSuccess($"Payjoin transaction broadcasted: {txid}", txid);
        }
        catch (SelfPayInvariantChecker.SelfPayInvariantException ex)
        {
            return MapRunTestPaymentException(request.InvoiceId, ex);
        }
        catch (RunTestPaymentService.RunTestPaymentExecutionException ex)
        {
            return MapRunTestPaymentException(request.InvoiceId, ex);
        }
    }

    internal ActionResult<RunTestPaymentResponse> MapRunTestPaymentException(string invoiceId, Exception exception)
    {
        return exception switch
        {
            SelfPayInvariantChecker.SelfPayInvariantException invariantException => RunTestPaymentInvariantFailure(invoiceId, invariantException.Message),
            RunTestPaymentService.RunTestPaymentExecutionException executionException => RunTestPaymentFailure(executionException.Message),
            _ => throw exception
        };
    }

    private OkObjectResult RunTestPaymentFailure(string message)
    {
        return Ok(RunTestPaymentResponse.Failure(message));
    }

    private OkObjectResult RunTestPaymentInvariantFailure(string invoiceId, string diagnosticMessage)
    {
        if (_logger is not null)
        {
            var (summary, diagnostics) = SelfPayInvariantChecker.SplitDiagnosticMessage(diagnosticMessage);
            LogRunTestPaymentInvariantFailed(_logger, invoiceId, summary, null);
            if (!string.IsNullOrWhiteSpace(diagnostics))
            {
                LogRunTestPaymentInvariantDiagnostics(_logger, invoiceId, diagnostics, null);
            }
        }

        return RunTestPaymentFailure($"Self-pay invariant failed: {diagnosticMessage}");
    }

    private OkObjectResult RunTestPaymentSuccess(string message, string transactionId)
    {
        return Ok(RunTestPaymentResponse.Success(message, transactionId));
    }
}
