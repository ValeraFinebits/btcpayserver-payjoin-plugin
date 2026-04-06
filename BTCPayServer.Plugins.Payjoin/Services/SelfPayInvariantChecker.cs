using NBitcoin;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BTCPayServer.Plugins.Payjoin.Services;

// These checks are specific to the diagnostic self-pay flow used by `RunTestPayment`.
// They intentionally guard against same-wallet anomalies and should not be treated as
// general payjoin invariants for external payer scenarios.
internal static class SelfPayInvariantChecker
{
    private const string DiagnosticsDelimiter = ". Diagnostics=";

    public static SelfPayBaseline CreateBaseline(Transaction senderTransaction, Script invoiceScript, Money invoiceAmount)
    {
        var invoiceOutputs = senderTransaction.Outputs
            .Where(output => output.ScriptPubKey == invoiceScript)
            .ToArray();
        if (invoiceOutputs.Length != 1)
        {
            throw CreateInvariantException("sender transaction must contain exactly one invoice output", senderTransaction, invoiceScript, invoiceAmount, senderTransaction.Inputs.Count, null);
        }

        if (invoiceOutputs[0].Value != invoiceAmount)
        {
            throw CreateInvariantException($"sender transaction invoice output amount mismatch. Expected={invoiceAmount.Satoshi} sats, Actual={invoiceOutputs[0].Value.Satoshi} sats", senderTransaction, invoiceScript, invoiceAmount, senderTransaction.Inputs.Count, null);
        }

        var changeOutputs = senderTransaction.Outputs
            .Where(output => output.ScriptPubKey != invoiceScript)
            .ToArray();
        if (changeOutputs.Length == 0)
        {
            throw CreateInvariantException("sender transaction must contain at least one non-invoice output", senderTransaction, invoiceScript, invoiceAmount, senderTransaction.Inputs.Count, null);
        }

        var senderChangeScriptCounts = changeOutputs
            .GroupBy(output => output.ScriptPubKey.ToString())
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        EnsureNoZeroOrDustOutputs("sender transaction", senderTransaction, invoiceScript, invoiceAmount, senderTransaction.Inputs.Count, senderChangeScriptCounts);

        return new SelfPayBaseline(invoiceScript, invoiceAmount, senderTransaction.Inputs.Count, senderChangeScriptCounts);
    }

    public static void ValidateProposal(Transaction proposalTransaction, SelfPayBaseline baseline)
    {
        ValidateTransaction("proposal transaction", proposalTransaction, baseline, requireReceiverContribution: true);
    }

    public static void ValidateFinalTransaction(Transaction finalTransaction, SelfPayBaseline baseline)
    {
        ValidateTransaction("final transaction", finalTransaction, baseline, requireReceiverContribution: true);
    }

    private static void ValidateTransaction(string stage, Transaction transaction, SelfPayBaseline baseline, bool requireReceiverContribution)
    {
        var invoiceOutputs = transaction.Outputs
            .Where(output => output.ScriptPubKey == baseline.InvoiceScript)
            .ToArray();
        if (invoiceOutputs.Length != 1)
        {
            throw CreateInvariantException($"{stage} must contain exactly one invoice output", transaction, baseline.InvoiceScript, baseline.InvoiceAmount, baseline.SenderInputCount, baseline.SenderChangeScriptCounts);
        }

        if (invoiceOutputs[0].Value != baseline.InvoiceAmount)
        {
            throw CreateInvariantException($"{stage} invoice output amount mismatch. Expected={baseline.InvoiceAmount.Satoshi} sats, Actual={invoiceOutputs[0].Value.Satoshi} sats", transaction, baseline.InvoiceScript, baseline.InvoiceAmount, baseline.SenderInputCount, baseline.SenderChangeScriptCounts);
        }

        if (requireReceiverContribution && transaction.Inputs.Count <= baseline.SenderInputCount)
        {
            throw CreateInvariantException($"{stage} must add at least one receiver input. SenderInputs={baseline.SenderInputCount}, ActualInputs={transaction.Inputs.Count}", transaction, baseline.InvoiceScript, baseline.InvoiceAmount, baseline.SenderInputCount, baseline.SenderChangeScriptCounts);
        }

        var reusedSenderChangeScript = transaction.Outputs
            .GroupBy(output => output.ScriptPubKey.ToString())
            .FirstOrDefault(group =>
                baseline.SenderChangeScriptCounts.TryGetValue(group.Key, out var originalCount) &&
                group.Count() > originalCount);
        if (reusedSenderChangeScript is not null)
        {
            throw CreateInvariantException($"{stage} reused a sender change script more times than the original sender transaction. Script={reusedSenderChangeScript.Key}, OriginalCount={baseline.SenderChangeScriptCounts[reusedSenderChangeScript.Key]}, ActualCount={reusedSenderChangeScript.Count()}", transaction, baseline.InvoiceScript, baseline.InvoiceAmount, baseline.SenderInputCount, baseline.SenderChangeScriptCounts);
        }

        EnsureNoZeroOrDustOutputs(stage, transaction, baseline.InvoiceScript, baseline.InvoiceAmount, baseline.SenderInputCount, baseline.SenderChangeScriptCounts);
    }

    private static void EnsureNoZeroOrDustOutputs(
        string stage,
        Transaction transaction,
        Script invoiceScript,
        Money invoiceAmount,
        int senderInputCount,
        IReadOnlyDictionary<string, int>? senderChangeScriptCounts)
    {
        var nonPositiveOutput = transaction.Outputs.FirstOrDefault(output => output.Value <= Money.Zero);
        if (nonPositiveOutput is not null)
        {
            throw CreateInvariantException($"{stage} contains a non-positive output: {FormatOutput(nonPositiveOutput)}", transaction, invoiceScript, invoiceAmount, senderInputCount, senderChangeScriptCounts);
        }

        var dustOutput = transaction.Outputs.FirstOrDefault(output => output.Value < output.GetDustThreshold());
        if (dustOutput is not null)
        {
            throw CreateInvariantException($"{stage} contains a dust output: {FormatOutput(dustOutput)}", transaction, invoiceScript, invoiceAmount, senderInputCount, senderChangeScriptCounts);
        }
    }

    private static SelfPayInvariantException CreateInvariantException(
        string message,
        Transaction transaction,
        Script invoiceScript,
        Money invoiceAmount,
        int senderInputCount,
        IReadOnlyDictionary<string, int>? senderChangeScriptCounts)
    {
        return new SelfPayInvariantException($"{message}{DiagnosticsDelimiter}{FormatDiagnostics(transaction, invoiceScript, invoiceAmount, senderInputCount, senderChangeScriptCounts)}");
    }

    public static (string Summary, string? Diagnostics) SplitDiagnosticMessage(string diagnosticMessage)
    {
        ArgumentNullException.ThrowIfNull(diagnosticMessage);

        var delimiterIndex = diagnosticMessage.IndexOf(DiagnosticsDelimiter, StringComparison.Ordinal);
        if (delimiterIndex < 0)
        {
            return (diagnosticMessage, null);
        }

        var summary = diagnosticMessage[..delimiterIndex];
        var diagnostics = diagnosticMessage[(delimiterIndex + DiagnosticsDelimiter.Length)..];
        return (summary, diagnostics.Length == 0 ? null : diagnostics);
    }

    private static string FormatDiagnostics(
        Transaction transaction,
        Script invoiceScript,
        Money invoiceAmount,
        int senderInputCount,
        IReadOnlyDictionary<string, int>? senderChangeScriptCounts)
    {
        var outputSummaries = transaction.Outputs
            .Select((output, index) =>
            {
                var script = output.ScriptPubKey.ToString();
                var role = output.ScriptPubKey == invoiceScript
                    ? "invoice"
                    : senderChangeScriptCounts is not null && senderChangeScriptCounts.ContainsKey(script)
                        ? "sender-change"
                        : "other";
                return $"#{index}:{output.Value.Satoshi}sats:role={role}:dust={output.GetDustThreshold().Satoshi}:script={script}";
            });

        return $"SenderInputs={senderInputCount}; ActualInputs={transaction.Inputs.Count}; ExpectedInvoiceAmount={invoiceAmount.Satoshi}; Inputs=[{string.Join(", ", transaction.Inputs.Select((input, index) => $"#{index}:{input.PrevOut}"))}]; Outputs=[{string.Join(", ", outputSummaries)}]";
    }

    private static string FormatOutput(TxOut output)
    {
        return $"Value={output.Value.Satoshi}sats, DustThreshold={output.GetDustThreshold().Satoshi}sats, Script={output.ScriptPubKey}";
    }

    internal sealed class SelfPayInvariantException : InvalidOperationException
    {
        public SelfPayInvariantException()
        {
        }

        public SelfPayInvariantException(string message) : base(message)
        {
        }

        public SelfPayInvariantException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

    internal sealed record SelfPayBaseline(
        Script InvoiceScript,
        Money InvoiceAmount,
        int SenderInputCount,
        IReadOnlyDictionary<string, int> SenderChangeScriptCounts);
}
