using System;
using System.Linq;
using NBitcoin;
using Newtonsoft.Json.Linq;
using BTCPayServer.Plugins.Payjoin.Data;
using System.Diagnostics.CodeAnalysis;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal static class PayjoinReceiverPaymentValidation
{
    internal sealed record OriginalPayment(long AmountSats, string TransactionId, uint OutputIndex);
    internal sealed record CommittedOutput(long ValueSats, string Script);
    internal sealed record CommittedOutputs(CommittedOutput[] Outputs, int? ChangeOutputIndex);

    internal static OriginalPayment? ValidateOriginalAmount(ulong? pin, Transaction original, byte[] receiverScript)
    {
        if (pin is not > 0 or > long.MaxValue) return null;
        var output = ResolveFallbackReceiverOutput(original, receiverScript);
        return output.Success && output.ValueSats == checked((long)pin.Value)
            ? new OriginalPayment(checked((long)pin.Value), original.GetHash().ToString(), output.OutputIndex!.Value)
            : null;
    }

    internal static string? FindPaymentConflict(OriginalPayment payment, PayjoinAccountingBridgeState bridge,
        CommittedOutputs? outputs, bool beforeOutputs, string? proposalTransactionId)
    {
        if (bridge.FailureMessage is not null || bridge.ReconciledAt is not null ||
            (bridge.ExpectedFinalTransactionId is null && (bridge.ExpectedFinalOutputIndex.HasValue || bridge.ExpectedFinalValueSats.HasValue)) ||
            (bridge.FallbackTransactionId is not null && bridge.FallbackTransactionId != payment.TransactionId) ||
            (bridge.FallbackOutputIndex.HasValue && bridge.FallbackOutputIndex != payment.OutputIndex) ||
            (bridge.FallbackValueSats.HasValue && bridge.FallbackValueSats != payment.AmountSats) ||
            (bridge.EffectiveInvoiceValueSats.HasValue && bridge.EffectiveInvoiceValueSats != payment.AmountSats) ||
            ((bridge.ExpectedFinalTransactionId is not null || bridge.SettlementScript is not null) &&
             (bridge.FallbackTransactionId is null || !bridge.FallbackOutputIndex.HasValue ||
              !bridge.FallbackValueSats.HasValue || !bridge.EffectiveInvoiceValueSats.HasValue)))
            return "persisted accounting data contradicts the original/session amount";
        if (!beforeOutputs && !CommittedOutputsAgree(outputs, bridge.SettlementScript, payment.AmountSats))
            return "persisted settlement contradicts the committed receiver outputs";
        if ((beforeOutputs && bridge.SettlementScript is not null) ||
            (bridge.ExpectedFinalTransactionId is not null &&
             !string.Equals(bridge.ExpectedFinalTransactionId, proposalTransactionId, StringComparison.OrdinalIgnoreCase)))
            return "persisted settlement belongs to another prepared proposal";
        return null;
    }

    internal static bool IsNegotiationAllowed([NotNullWhen(true)] PayjoinAccountingBridgeState? bridge) =>
        bridge?.Status is PayjoinAccountingBridgeStatus.PendingFallback or PayjoinAccountingBridgeStatus.PendingFinalTransaction;

    internal static CommittedOutputs? ReadCommittedOutputs(string[] events)
    {
        // TODO: Add a typed rust-payjoin/payjoin-ffi API for committed outputs and change_vout; replace JSON parsing.
        var commitments = events.Select(Newtonsoft.Json.Linq.JObject.Parse)
            .Select(item => item["CommittedOutputs"]).Where(item => item is not null).ToArray();
        if (commitments.Length == 0) return null;
        if (commitments.Length != 1 || commitments[0] is not JObject committed || committed["outputs"] is not JArray outputs)
            throw new FormatException("Unrecognized committed receiver outputs.");
        return new CommittedOutputs(outputs.Select(output => new CommittedOutput(
            output["value"]?.Value<long>() ?? throw new FormatException("Missing committed output value."),
            output["script_pubkey"]?.Value<string>() ?? throw new FormatException("Missing committed output script."))).ToArray(),
            committed["change_vout"]?.Value<int>());
    }

    internal static bool CommittedOutputsAgree(CommittedOutputs? committed, string? settlementScript, long pin)
    {
        if (committed is null || settlementScript is null || committed.ChangeOutputIndex is not { } index ||
            index < 0 || index >= committed.Outputs.Length) return false;
        var output = committed.Outputs[index];
        return output.ValueSats == pin && string.Equals(output.Script, settlementScript, StringComparison.OrdinalIgnoreCase) &&
            committed.Outputs.Count(item => string.Equals(item.Script, settlementScript, StringComparison.OrdinalIgnoreCase)) == 1;
    }

    internal static FallbackReceiverOutputMatch ResolveFallbackReceiverOutput(Transaction fallbackTransaction, byte[] receiverScript)
    {
        var receiverScriptPubKey = Script.FromBytesUnsafe(receiverScript);
        var matches = fallbackTransaction.Outputs
            .Select((output, index) => new { output, index })
            .Where(x => x.output.ScriptPubKey == receiverScriptPubKey)
            .ToArray();

        return matches.Length switch
        {
            1 => FallbackReceiverOutputMatch.FoundMatch(checked((uint)matches[0].index), matches[0].output.Value.Satoshi),
            0 => FallbackReceiverOutputMatch.NotFound(),
            _ => FallbackReceiverOutputMatch.Ambiguous()
        };
    }

    internal readonly record struct FallbackReceiverOutputMatch(uint? OutputIndex, long? ValueSats, FallbackReceiverOutputMatchStatus Status)
    {
        internal bool Success => Status == FallbackReceiverOutputMatchStatus.Found;

        internal static FallbackReceiverOutputMatch FoundMatch(uint outputIndex, long valueSats)
        {
            return new FallbackReceiverOutputMatch(outputIndex, valueSats, FallbackReceiverOutputMatchStatus.Found);
        }

        internal static FallbackReceiverOutputMatch NotFound()
        {
            return new FallbackReceiverOutputMatch(null, null, FallbackReceiverOutputMatchStatus.NotFound);
        }

        internal static FallbackReceiverOutputMatch Ambiguous()
        {
            return new FallbackReceiverOutputMatch(null, null, FallbackReceiverOutputMatchStatus.Ambiguous);
        }
    }

    internal enum FallbackReceiverOutputMatchStatus
    {
        Found,
        NotFound,
        Ambiguous
    }
}
