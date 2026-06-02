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
    public async Task<ActionResult<GetBip21Response>> GetInvoicePaymentUrl(string invoiceId, CancellationToken cancellationToken)
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

        return Ok(paymentUrl);
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
        if (storeSettings is null || storeSettings.OhttpRelayUrl is null)
        {
            return RunTestPaymentFailure("OhttpRelayUrl not found");
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
            canonicalPaymentUrl,
            storeSettings.OhttpRelayUrl,
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
}
