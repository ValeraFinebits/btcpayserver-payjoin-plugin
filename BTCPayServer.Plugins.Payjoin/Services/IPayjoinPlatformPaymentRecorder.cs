using BTCPayServer.Data;
using BTCPayServer.Services.Invoices;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

// Narrows the platform PaymentService (whose methods are not overridable) so accounting
// reconciliation can be exercised end to end in tests against an in-memory payment store.
internal interface IPayjoinPlatformPaymentRecorder
{
    Task<PaymentEntity?> AddPaymentAsync(PaymentData paymentData, HashSet<string> searchTerms);

    Task UpdatePaymentsAsync(List<PaymentEntity> payments);
}

internal sealed class PayjoinPlatformPaymentRecorder : IPayjoinPlatformPaymentRecorder
{
    private readonly PaymentService _paymentService;

    public PayjoinPlatformPaymentRecorder(PaymentService paymentService)
    {
        _paymentService = paymentService;
    }

    public Task<PaymentEntity?> AddPaymentAsync(PaymentData paymentData, HashSet<string> searchTerms)
    {
        return _paymentService.AddPayment(paymentData, searchTerms)!;
    }

    public Task UpdatePaymentsAsync(List<PaymentEntity> payments)
    {
        return _paymentService.UpdatePayments(payments);
    }
}
