using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Filters;
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
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using PayjoinUri = Payjoin.Uri;

namespace BTCPayServer.Plugins.Payjoin.Controllers;

[Route("~/plugins/payjoin")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanViewProfile)]
public class UIPayJoinController : Controller
{
    private static readonly Action<ILogger, string, string, Exception?> LogPayjoinSenderBroadcasted =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(1, nameof(LogPayjoinSenderBroadcasted)),
            "Payjoin sender broadcasted payjoin transaction {TransactionId} for {InvoiceId}");

    private static readonly Action<ILogger, string, Exception?> LogRunTestPaymentFailed =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(2, nameof(LogRunTestPaymentFailed)),
            "Payjoin test payment for {InvoiceId} failed with an unexpected exception");

    private readonly BTCPayServerEnvironment _env;
    private readonly InvoiceRepository _invoiceRepository;
    private readonly StoreRepository _storeRepository;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly IPayjoinStoreSettingsRepository _storeSettingsRepository;
    private readonly IPayjoinInvoicePaymentUrlService _paymentUrlService;
    private readonly IRunTestPaymentService _runTestPaymentService;
    private readonly ILogger<UIPayJoinController>? _logger;

    public UIPayJoinController(
        BTCPayServerEnvironment env,
        InvoiceRepository invoiceRepository,
        StoreRepository storeRepository,
        PaymentMethodHandlerDictionary handlers,
        BTCPayNetworkProvider networkProvider,
        IPayjoinStoreSettingsRepository storeSettingsRepository,
        IPayjoinInvoicePaymentUrlService paymentUrlService,
        IRunTestPaymentService runTestPaymentService,
        ILogger<UIPayJoinController>? logger = null)
    {
        _env = env;
        _invoiceRepository = invoiceRepository;
        _storeRepository = storeRepository;
        _handlers = handlers;
        _networkProvider = networkProvider;
        _storeSettingsRepository = storeSettingsRepository;
        _paymentUrlService = paymentUrlService;
        _runTestPaymentService = runTestPaymentService;
        _logger = logger;
    }

    [AllowAnonymous]
    [HttpGet("invoices/{invoiceId}/payment-url")]
    // TODO: rate-limit this route before release. It is anonymous and every call runs BuildAsync, which
    // queries the wallet's UTXOs and, with no session yet, bootstraps OHTTP against third-party relays. The
    // only limit today is client-side (maxPayjoinRequestAttempts in PayJoinBitcoinCheckoutEnd.cshtml), so it
    // bounds a well-behaved checkout and nothing else. Per-invoice rate limit, or cache a failed bootstrap
    // per store for a few tens of seconds.
    public async Task<ActionResult<PayjoinCheckoutAvailabilityResponse>> GetInvoicePaymentUrl(string invoiceId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(invoiceId))
        {
            return NotFound();
        }

        var paymentUrl = await _paymentUrlService.GetInvoicePaymentUrlAsync(invoiceId, cancellationToken).ConfigureAwait(false);
        if (paymentUrl is null)
        {
            return NotFound();
        }

        return Ok(PayjoinCheckoutAvailabilityResponse.From(paymentUrl));
    }

    // TODO: Remove this test endpoint.
    [CheatModeRoute]
    // This cheat-mode-only flow exercises payjoin using a dedicated payer wallet.
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    [HttpPost("run-test-payment")]
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Any exception escaping a plugin controller makes BTCPay disable the plugin and stop the host process.")]
    public async Task<ActionResult<RunTestPaymentResponse>> RunTestPayment([FromBody] RunTestPaymentRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(RunTestPaymentResponse.Failure("A JSON body containing an invoiceId is required."));
        }

        if (string.IsNullOrWhiteSpace(request.InvoiceId))
        {
            return RunTestPaymentFailure("invoiceId is required");
        }

        try
        {
            return await RunTestPaymentCoreAsync(request.InvoiceId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_logger is not null)
            {
                LogRunTestPaymentFailed(_logger, request.InvoiceId, ex);
            }

            return RunTestPaymentFailure($"The test payment for invoice {request.InvoiceId} failed unexpectedly: {ex.Message}");
        }
    }

    private async Task<ActionResult<RunTestPaymentResponse>> RunTestPaymentCoreAsync(string invoiceId, CancellationToken cancellationToken)
    {
        var invoicePaymentUrl = await _paymentUrlService.GetInvoicePaymentUrlAsync(invoiceId, cancellationToken).ConfigureAwait(false);
        if (invoicePaymentUrl is null)
        {
            return RunTestPaymentFailure($"No payjoin payment URL is available for invoice {invoiceId}. The invoice is not payable or has no Bitcoin payment method.");
        }

        if (invoicePaymentUrl.Status != PayjoinAvailabilityStatus.Active)
        {
            return RunTestPaymentFailure(invoicePaymentUrl.UnavailableReason ?? "payjoin is not available for invoice");
        }

        if (!System.Uri.TryCreate(invoicePaymentUrl.Bip21, UriKind.Absolute, out var canonicalPaymentUrl))
        {
            return RunTestPaymentFailure("invoice paymentUrl invalid");
        }

        var invoice = await _invoiceRepository.GetInvoice(invoiceId).ConfigureAwait(false);
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
        var ohttpRelayUrls = storeSettings?.GetEffectiveOhttpRelayUrls();
        if (ohttpRelayUrls is null || ohttpRelayUrls.Count == 0)
        {
            return RunTestPaymentFailure("no OHTTP relay URLs configured");
        }

        var network = _networkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode);
        if (network is null)
        {
            return RunTestPaymentFailure("network not available");
        }

        string paymentAddress;
        decimal paymentAmount;
        try
        {
            using var parsedUri = PayjoinUri.Parse(canonicalPaymentUrl.AbsoluteUri);
            paymentAddress = parsedUri.Address();
            var amountSats = parsedUri.AmountSats();
            if (amountSats is null)
            {
                return RunTestPaymentFailure("payment amount missing in paymentUrl");
            }

            paymentAmount = Money.Satoshis(checked((long)amountSats.Value)).ToDecimal(MoneyUnit.BTC);
            using var _ = parsedUri.CheckPjSupported();
        }
        catch (UriParseException ex)
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
            invoiceId,
            canonicalPaymentUrl,
            ohttpRelayUrls,
            paymentAddressValue,
            paymentAmount,
            network);

        try
        {
            var txid = await _runTestPaymentService.ExecuteAsync(runTestPaymentContext, cancellationToken).ConfigureAwait(false);
            if (_logger is not null)
            {
                LogPayjoinSenderBroadcasted(_logger, txid, invoiceId, null);
            }

            return RunTestPaymentSuccess($"Payjoin transaction broadcasted: {txid}", txid);
        }
        catch (RunTestPaymentService.RunTestPaymentExecutionException ex)
        {
            return RunTestPaymentFailure(ex.Message);
        }
    }

    private OkObjectResult RunTestPaymentFailure(string message)
    {
        return Ok(RunTestPaymentResponse.Failure(message));
    }

    private OkObjectResult RunTestPaymentSuccess(string message, string transactionId)
    {
        return Ok(RunTestPaymentResponse.Success(message, transactionId));
    }
}
