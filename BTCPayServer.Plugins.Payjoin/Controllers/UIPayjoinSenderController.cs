using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Plugins.Wallets;
using BTCPayServer.Plugins.Wallets.Views.ViewModels;
using BTCPayServer.Services;
using BTCPayServer.Services.Wallets;
using BTCPayServer.Plugins.Payjoin.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Controllers;

[Route("~/stores/{storeId}/payjoin/send")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
public class UIPayjoinSenderController : Controller
{
    private readonly PayjoinSenderService _senderService;
    private readonly PayjoinSenderSessionStore _senderSessionStore;
    private readonly IPayjoinSenderSessionProcessor _senderSessionProcessor;
    private readonly WalletRepository _walletRepository;

    internal UIPayjoinSenderController(
        PayjoinSenderService senderService,
        PayjoinSenderSessionStore senderSessionStore,
        IPayjoinSenderSessionProcessor senderSessionProcessor,
        WalletRepository walletRepository)
    {
        _senderService = senderService;
        _senderSessionStore = senderSessionStore;
        _senderSessionProcessor = senderSessionProcessor;
        _walletRepository = walletRepository;
    }

    /// <summary>
    /// Starts a session from BTCPay's own send screen. The screen posts its whole form here, so
    /// the operator keeps coin selection, labels, fee-rate presets, the balance and the fiat
    /// conversion, and this plugin only takes over the part core cannot do: the asynchronous
    /// round with a receiver who is not online.
    /// </summary>
    [HttpPost("from-wallet")]
    // The operator arrives from BTCPay's send screen, so this asks for the permission that screen
    // asks for rather than the store-settings permission the rest of this controller uses.
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = WalletPolicies.CanCreateWalletTransactions)]
    public async Task<IActionResult> SendFromWallet(string storeId, WalletSendModel model, string? asyncPayjoinBip21, CancellationToken cancellationToken)
    {
        System.ArgumentNullException.ThrowIfNull(model);
        // The send screen carries the URI twice. Core's PayJoinBIP21 field drives the v1 flow,
        // which a v2 endpoint does not answer, so the extension clears it in the browser and
        // posts the URI under its own name instead. The model's copy stays as a fallback for
        // callers that post the form directly.
        if (!string.IsNullOrEmpty(asyncPayjoinBip21))
        {
            model.PayJoinBIP21 = asyncPayjoinBip21;
        }

        var walletId = new WalletId(storeId, PayjoinConstants.BitcoinCode);
        var destination = ResolveSingleDestination(model, out var destinationError);
        if (destination is null)
        {
            return RedirectWithError(storeId, walletId, destinationError!);
        }

        await SaveDestinationLabelsAsync(walletId, destination).ConfigureAwait(false);

        var result = await _senderService.StartAsync(
            storeId,
            model.PayJoinBIP21,
            model.FeeSatoshiPerByte,
            new BTCPayServer.Abstractions.RequestBaseUrl(Request),
            model.InputSelection ? model.SelectedInputs?.ToArray() : null,
            cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return RedirectWithError(storeId, walletId, result.Error!);
        }

        return RedirectAfterStart(storeId, result);
    }

    /// <summary>
    /// An async payjoin pays one destination, because the library takes its fee contribution from
    /// the first output that is not the payee, and with a second payee that output would be
    /// someone else's payment rather than this wallet's change.
    /// </summary>
    internal static WalletSendModel.TransactionOutput? ResolveSingleDestination(WalletSendModel model, out string? error)
    {
        if (string.IsNullOrEmpty(model.PayJoinBIP21))
        {
            error = "This destination does not advertise async payjoin.";
            return null;
        }

        var outputs = model.Outputs?.Where(x => !string.IsNullOrWhiteSpace(x.DestinationAddress)).ToArray() ?? [];
        if (outputs.Length != 1)
        {
            error = "An async payjoin pays one destination. Remove the other destinations, or send them separately.";
            return null;
        }

        if (outputs[0].SubtractFeesFromOutput)
        {
            error = "An async payjoin cannot subtract the fee from the amount the receiver expects.";
            return null;
        }

        // The payjoin pays what the URI says. The fields on the screen stay editable after the
        // URI resolves, so an edit there would otherwise be silently ignored.
        var uriAddress = System.Text.RegularExpressions.Regex.Match(
            model.PayJoinBIP21, "^bitcoin:([^?]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (uriAddress.Success &&
            !string.Equals(System.Uri.UnescapeDataString(uriAddress.Groups[1].Value), outputs[0].DestinationAddress?.Trim(), System.StringComparison.OrdinalIgnoreCase))
        {
            error = "The destination no longer matches the payment link. Paste the link again to send an async payjoin.";
            return null;
        }

        var uriAmount = System.Text.RegularExpressions.Regex.Match(
            model.PayJoinBIP21, "[?&]amount=([0-9.]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (uriAmount.Success &&
            decimal.TryParse(uriAmount.Groups[1].Value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var expectedAmount) &&
            outputs[0].Amount is decimal formAmount &&
            formAmount != expectedAmount)
        {
            error = "The amount no longer matches the payment link. An async payjoin pays the amount the link asks for.";
            return null;
        }

        error = null;
        return outputs[0];
    }

    /// <summary>
    /// Keeps the labels the operator typed on the send screen, the way core's own send does:
    /// they attach to the destination address, not to the transaction.
    /// </summary>
    private async Task SaveDestinationLabelsAsync(WalletId walletId, WalletSendModel.TransactionOutput destination)
    {
        var labels = destination.Labels?.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray() ?? [];
        if (labels.Length == 0)
        {
            return;
        }

        var walletObjectAddress = new WalletObjectId(walletId, WalletObjectData.Types.Address, destination.DestinationAddress);
        if (await _walletRepository.GetWalletObject(walletObjectAddress).ConfigureAwait(false) is null)
        {
            await _walletRepository.EnsureWalletObject(walletObjectAddress).ConfigureAwait(false);
        }

        await _walletRepository.AddWalletObjectLabels(walletObjectAddress, labels).ConfigureAwait(false);
    }

    private IActionResult RedirectWithError(string storeId, WalletId walletId, string error)
    {
        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Severity = StatusMessageModel.StatusSeverity.Error,
            Message = error
        });
        return RedirectToAction("WalletSend", "UIWallets", new { area = "Wallets", walletId = walletId.ToString() });
    }

    [HttpGet]
    public IActionResult Send(string storeId)
    {
        return View(BuildViewModel(storeId));
    }

    private IActionResult RedirectAfterStart(string storeId, PayjoinSenderStartResult result)
    {
        if (result.PendingTransactionId is not null)
        {
            // The wallet cannot sign on the server, so BTCPay's own screen collects the signature
            // from the vault, a hardware device, a seed or the other multisig signers. Nothing
            // reaches the payjoin directory until that signature arrives.
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Severity = StatusMessageModel.StatusSeverity.Info,
                Message = "The payment is ready to sign. The payjoin session starts as soon as the transaction is signed."
            });
            return RedirectToAction(
                "ViewPendingTransaction",
                "UIWallets",
                new
                {
                    area = "Wallets",
                    walletId = new WalletId(storeId, PayjoinConstants.BitcoinCode).ToString(),
                    pendingTransactionId = result.PendingTransactionId
                });
        }

        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Severity = StatusMessageModel.StatusSeverity.Success,
            Message = $"Payjoin sender session started. The payment completes in the background; the fallback transaction id is {result.OriginalTransactionId}."
        });
        return RedirectToAction(nameof(Send), new { storeId });
    }

    [HttpPost("{senderSessionId}/cancel")]
    public async Task<IActionResult> Cancel(string storeId, string senderSessionId, CancellationToken cancellationToken)
    {
        var result = await _senderSessionProcessor
            .CancelAsync(storeId, senderSessionId, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Success)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Severity = StatusMessageModel.StatusSeverity.Error,
                Message = result.Error
            });
            return RedirectToAction(nameof(Send), new { storeId });
        }

        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Severity = StatusMessageModel.StatusSeverity.Success,
            Message = result.BroadcastTransactionId is null
                ? "The payjoin session ended. Nothing was broadcast, so the coins are free again."
                : $"The payjoin session ended and the plain payment was broadcast as {result.BroadcastTransactionId}."
        });
        return RedirectToAction(nameof(Send), new { storeId });
    }

    private PayjoinSenderViewModel BuildViewModel(string storeId)
    {
        return new PayjoinSenderViewModel
        {
            StoreId = storeId,
            WalletId = new WalletId(storeId, PayjoinConstants.BitcoinCode).ToString(),
            Sessions = _senderSessionStore.GetSessions(storeId)
                .OrderByDescending(x => x.CreatedAt)
                .Select(x => new PayjoinSenderSessionViewModel
                {
                    SenderSessionId = x.SenderSessionId,
                    DestinationAddress = x.DestinationAddress,
                    AmountSats = x.AmountSats,
                    Status = x.Status switch
                    {
                        PayjoinSenderSessionStatus.Pending => "Pending",
                        PayjoinSenderSessionStatus.CompletedPayjoin => "Completed (payjoin)",
                        PayjoinSenderSessionStatus.CompletedFallback => "Completed (fallback)",
                        PayjoinSenderSessionStatus.Failed => "Failed",
                        PayjoinSenderSessionStatus.AwaitingSignature => "Waiting for signature",
                        _ => x.Status.ToString()
                    },
                    PendingTransactionId = x.Status == PayjoinSenderSessionStatus.AwaitingSignature
                        ? x.PendingTransactionId
                        : null,
                    CanCancel = x.Status is PayjoinSenderSessionStatus.Pending
                        or PayjoinSenderSessionStatus.AwaitingSignature,
                    BroadcastTransactionId = x.BroadcastTransactionId,
                    FailureMessage = x.FailureMessage,
                    CreatedAt = x.CreatedAt
                })
                .ToArray()
        };
    }
}
