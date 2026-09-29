using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Filters;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
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

    private static readonly Action<ILogger, string, Exception?> LogSeedAttentionRecordFailed =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(3, nameof(LogSeedAttentionRecordFailed)),
            "Seeding a payjoin settlement record for {InvoiceId} failed with an unexpected exception");

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
    public async Task<ActionResult<GetCheckoutBip21Response>> GetInvoicePaymentUrl(string invoiceId, CancellationToken cancellationToken)
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

        return Ok(ToCheckoutResponse(paymentUrl));
    }

    private static GetCheckoutBip21Response ToCheckoutResponse(GetBip21Response paymentUrl)
    {
        return new GetCheckoutBip21Response
        {
            Bip21 = paymentUrl.Bip21,
            Status = paymentUrl.Status == PayjoinAvailabilityStatus.Active
                ? PayjoinCheckoutAvailabilityStatus.Active
                : PayjoinCheckoutAvailabilityStatus.Unavailable
        };
    }

    // TODO: Remove this test endpoint.
    [CheatModeRoute]
    // This cheat-mode-only flow exercises payjoin using a dedicated payer wallet.
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    [HttpPost("run-test-payment")]
    public async Task<ActionResult<RunTestPaymentResponse>> RunTestPayment([FromBody] RunTestPaymentRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.InvoiceId))
        {
            return RunTestPaymentFailure("invoiceId is required");
        }

        var invoicePaymentUrl = await _paymentUrlService.GetInvoicePaymentUrlAsync(request.InvoiceId, cancellationToken).ConfigureAwait(false);
        if (invoicePaymentUrl is null)
        {
            return RunTestPaymentFailure("paymentUrl not available for invoice");
        }

        if (invoicePaymentUrl.Status != PayjoinAvailabilityStatus.Active)
        {
            return RunTestPaymentFailure(invoicePaymentUrl.UnavailableReason ?? "payjoin is not available for invoice");
        }

        if (!System.Uri.TryCreate(invoicePaymentUrl.Bip21, UriKind.Absolute, out var canonicalPaymentUrl))
        {
            return RunTestPaymentFailure("invoice paymentUrl invalid");
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
            using var parsedUri = PayjoinUri.Parse(canonicalPaymentUrl.ToString());
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
            request.InvoiceId,
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
                LogPayjoinSenderBroadcasted(_logger, txid, request.InvoiceId, null);
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

    // TODO: Remove this test endpoint.
    [CheatModeRoute]
    // The overview's attention table lists failed settlement records, and expired ones still armed
    // with an expected final transaction. Neither state can be produced through the UI: the first
    // needs reconciliation to hit contradictory payment data, the second needs a bridge older than
    // ArmedBridgeGracePeriod. This seeds them so the table and its Retry action can be exercised.
    [HttpPost("seed-attention-record")]
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Any exception escaping a plugin controller makes BTCPay disable the plugin and stop the host process.")]
    public async Task<ActionResult<SeedAttentionRecordResponse>> SeedAttentionRecord(
        [FromBody] SeedAttentionRecordRequest request,
        [FromServices] IPayjoinInvoiceLookup invoiceLookup,
        [FromServices] IAuthorizationService authorizationService,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(SeedAttentionRecordResponse.Failure("A JSON body containing an invoiceId is required."));
        }

        ArgumentNullException.ThrowIfNull(invoiceLookup);
        ArgumentNullException.ThrowIfNull(authorizationService);

        if (string.IsNullOrWhiteSpace(request.InvoiceId))
        {
            return Ok(SeedAttentionRecordResponse.Failure("invoiceId is required"));
        }

        var kind = (request.Kind ?? "failed").Trim().ToUpperInvariant();
        if (kind is not ("FAILED" or "EXPIRED"))
        {
            return Ok(SeedAttentionRecordResponse.Failure($"Unknown kind '{request.Kind}'. Use 'failed' or 'expired'."));
        }

        try
        {
            return await SeedAttentionRecordCoreAsync(
                    request.InvoiceId,
                    kind,
                    invoiceLookup,
                    authorizationService,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_logger is not null)
            {
                LogSeedAttentionRecordFailed(_logger, request.InvoiceId, ex);
            }

            return Ok(SeedAttentionRecordResponse.Failure($"Seeding a settlement record for invoice {request.InvoiceId} failed unexpectedly: {ex.Message}"));
        }
    }

    private async Task<ActionResult<SeedAttentionRecordResponse>> SeedAttentionRecordCoreAsync(
        string invoiceId,
        string kind,
        IPayjoinInvoiceLookup invoiceLookup,
        IAuthorizationService authorizationService,
        CancellationToken cancellationToken)
    {
        var invoice = await invoiceLookup.GetInvoiceAsync(invoiceId).ConfigureAwait(false);
        if (invoice is null)
        {
            return Ok(SeedAttentionRecordResponse.Failure($"No invoice {invoiceId} exists on this server."));
        }

        var authorization = await authorizationService
            .AuthorizeAsync(User, invoice.StoreId, Policies.CanModifyStoreSettings)
            .ConfigureAwait(false);
        if (!authorization.Succeeded)
        {
            return Forbid();
        }

        var recordSeeder = HttpContext.RequestServices.GetRequiredService<IPayjoinAttentionRecordSeeder>();
        var seedKind = kind == "FAILED"
            ? PayjoinAttentionRecordSeedKind.Failed
            : PayjoinAttentionRecordSeedKind.Expired;
        var seededStatus = await recordSeeder.TrySeedAttentionRecordAsync(
            new SeedPayjoinAttentionRecordRequest(
                invoiceId,
                invoice.StoreId,
                PayjoinConstants.BitcoinCode,
                PaymentTypes.CHAIN.GetPaymentMethodId(PayjoinConstants.BitcoinCode).ToString(),
                seedKind,
                DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
        if (seededStatus is null)
        {
            return Ok(SeedAttentionRecordResponse.Failure(
                $"Invoice {invoiceId} already has a settlement record; refusing to overwrite it."));
        }

        var stateDescription = seedKind == PayjoinAttentionRecordSeedKind.Failed ? "Failed" : "armed Expired";
        return Ok(SeedAttentionRecordResponse.Success(
            $"Settlement record for invoice {invoiceId} seeded as {stateDescription}.",
            seededStatus.Value.ToString()));
    }
}
