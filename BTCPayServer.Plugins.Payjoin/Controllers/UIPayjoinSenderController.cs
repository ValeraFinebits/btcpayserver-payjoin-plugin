using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Plugins.Payjoin.Models;
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

    internal UIPayjoinSenderController(
        PayjoinSenderService senderService,
        PayjoinSenderSessionStore senderSessionStore)
    {
        _senderService = senderService;
        _senderSessionStore = senderSessionStore;
    }

    [HttpGet]
    public IActionResult Send(string storeId, string? bip21)
    {
        return View(BuildViewModel(storeId, bip21));
    }

    [HttpPost]
    public async Task<IActionResult> Send(string storeId, PayjoinSenderViewModel model, CancellationToken cancellationToken)
    {
        System.ArgumentNullException.ThrowIfNull(model);
        var result = await _senderService.StartAsync(storeId, model.Bip21 ?? string.Empty, model.FeeRateSatPerVb, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Severity = StatusMessageModel.StatusSeverity.Error,
                Message = result.Error
            });
            return View(BuildViewModel(storeId, model.Bip21));
        }

        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Severity = StatusMessageModel.StatusSeverity.Success,
            Message = $"Payjoin sender session started. The payment completes in the background; the fallback transaction id is {result.OriginalTransactionId}."
        });
        return RedirectToAction(nameof(Send), new { storeId });
    }

    private PayjoinSenderViewModel BuildViewModel(string storeId, string? bip21)
    {
        return new PayjoinSenderViewModel
        {
            Bip21 = bip21,
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
                        _ => x.Status.ToString()
                    },
                    BroadcastTransactionId = x.BroadcastTransactionId,
                    FailureMessage = x.FailureMessage,
                    CreatedAt = x.CreatedAt
                })
                .ToArray()
        };
    }
}
