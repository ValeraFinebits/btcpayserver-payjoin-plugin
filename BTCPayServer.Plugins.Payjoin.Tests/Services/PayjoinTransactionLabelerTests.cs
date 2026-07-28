using BTCPayServer.Plugins.Payjoin.Services;
using Xunit;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinTransactionLabelerTests
{
    [Fact]
    public void CreateAsyncPayjoinAttachmentUsesTheProductLabel()
    {
        var attachment = PayjoinTransactionLabeler.CreateAsyncPayjoinAttachment();

        Assert.Equal("Async Payjoin", attachment.Type);
    }

    [Fact]
    public void CreateSettlementAttachmentsAddsTheStandardInvoiceLabelWhenAnInvoiceIsPresent()
    {
        var attachments = PayjoinTransactionLabeler.CreateSettlementAttachments("invoice-1");

        Assert.Collection(
            attachments,
            invoice =>
            {
                Assert.Equal("invoice", invoice.Type);
                Assert.Equal("invoice-1", invoice.Id);
            },
            asyncPayjoin => Assert.Equal("Async Payjoin", asyncPayjoin.Type));
    }

    [Fact]
    public void CreateSettlementAttachmentsOmitsTheInvoiceLabelWhenNoInvoice()
    {
        var attachments = PayjoinTransactionLabeler.CreateSettlementAttachments("");

        var only = Assert.Single(attachments);
        Assert.Equal("Async Payjoin", only.Type);
    }
}
