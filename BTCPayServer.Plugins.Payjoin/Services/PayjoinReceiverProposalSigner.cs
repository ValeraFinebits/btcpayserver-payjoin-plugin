using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using BTCPayServer.Services.Wallets;
using NBitcoin;
using NBXplorer;
using Payjoin;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal sealed class PayjoinReceiverProposalSigner : IPayjoinReceiverProposalSigner
{
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly StoreRepository _storeRepository;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly ExplorerClientProvider _explorerClientProvider;

    public PayjoinReceiverProposalSigner(
        BTCPayNetworkProvider networkProvider,
        StoreRepository storeRepository,
        PaymentMethodHandlerDictionary handlers,
        ExplorerClientProvider explorerClientProvider)
    {
        _networkProvider = networkProvider;
        _storeRepository = storeRepository;
        _handlers = handlers;
        _explorerClientProvider = explorerClientProvider;
    }

    public async Task<string> SignProposalAsync(
        ProvisionalProposal proposal,
        string storeId,
        ReceivedCoin[] receiverCoins,
        CancellationToken cancellationToken)
    {
        var network = _networkProvider.GetNetwork<BTCPayNetwork>("BTC") ?? throw new InvalidOperationException("BTC network not available");
        var store = await _storeRepository.FindStore(storeId).ConfigureAwait(false) ?? throw new InvalidOperationException($"Store {storeId} not found");
        var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId("BTC");
        var derivationScheme = store.GetPaymentMethodConfig<DerivationSchemeSettings>(paymentMethodId, _handlers, true) ?? throw new InvalidOperationException("Derivation scheme not configured for BTC");

        if (!derivationScheme.IsHotWallet)
        {
            throw new InvalidOperationException("Cannot sign payjoin proposal from a cold wallet");
        }

        var client = _explorerClientProvider.GetExplorerClient(network);
        var signingKeyStr = await client.GetMetadataAsync<string>(
            derivationScheme.AccountDerivation,
            WellknownMetadataKeys.MasterHDKey,
            cancellationToken).ConfigureAwait(false);

        if (signingKeyStr is null)
        {
            throw new InvalidOperationException("Wallet seed not available");
        }

        var signingKey = ExtKey.Parse(signingKeyStr, network.NBitcoinNetwork);
        var signingKeySettings = derivationScheme.GetAccountKeySettingsFromRoot(signingKey) ?? throw new InvalidOperationException("Wallet key settings not available");
        var rootedKeyPath = signingKeySettings.GetRootedKeyPath() ?? throw new InvalidOperationException("Wallet key path mismatch");
        var accountKey = signingKey.Derive(rootedKeyPath.KeyPath);
        var psbtToSign = proposal.PsbtToSign();
        var proposalPsbt = PSBT.Parse(psbtToSign, network.NBitcoinNetwork);

        var updated = await client.UpdatePSBTAsync(new NBXplorer.Models.UpdatePSBTRequest
        {
            PSBT = proposalPsbt,
            DerivationScheme = derivationScheme.AccountDerivation
        }, cancellationToken).ConfigureAwait(false);
        if (updated?.PSBT is not null)
        {
            proposalPsbt = updated.PSBT;
        }

        EnsureContributedInputsPresent(proposalPsbt, receiverCoins);
        derivationScheme.RebaseKeyPaths(proposalPsbt);
        proposalPsbt.RebaseKeyPaths(signingKeySettings.AccountKey, rootedKeyPath);
        proposalPsbt.SignAll(derivationScheme.AccountDerivation, accountKey, rootedKeyPath);
        FinalizeContributedInputs(proposalPsbt, receiverCoins);
        ClearSenderInputFinalization(proposalPsbt, receiverCoins);
        ClearPartialSignatures(proposalPsbt);
        ClearHdKeyPaths(proposalPsbt);

        return proposalPsbt.ToBase64();
    }

    public static void EnsureContributedInputsPresent(PSBT proposalPsbt, ReceivedCoin[] receiverCoins)
    {
        var missingInputs = receiverCoins
            .Where(receiverCoin => proposalPsbt.Inputs.All(input => input.PrevOut != receiverCoin.OutPoint))
            .Select(receiverCoin => receiverCoin.OutPoint.ToString())
            .ToArray();

        if (missingInputs.Length == 0)
        {
            return;
        }

        throw new InvalidOperationException($"Provisional proposal is missing contributed receiver inputs: {string.Join(", ", missingInputs)}");
    }

    private static void FinalizeContributedInputs(PSBT proposalPsbt, ReceivedCoin[] receiverCoins)
    {
        // TODO: Collapse the receiver-input finalization and sender-input cleanup steps into a single proposal-normalization pipeline if this PSBT policy grows further.
        foreach (var input in proposalPsbt.Inputs)
        {
            if (!IsContributedReceiverInput(input.PrevOut, receiverCoins))
            {
                continue;
            }

            if (!input.TryFinalizeInput(out _))
            {
                throw new InvalidOperationException($"Receiver input '{input.PrevOut}' could not be finalized.");
            }
        }
    }

    public static void ClearSenderInputFinalization(PSBT proposalPsbt, ReceivedCoin[] receiverCoins)
    {
        foreach (var input in proposalPsbt.Inputs)
        {
            if (IsContributedReceiverInput(input.PrevOut, receiverCoins))
            {
                continue;
            }

            input.FinalScriptSig = null;
            input.FinalScriptWitness = null;
        }
    }

    private static bool IsContributedReceiverInput(OutPoint prevOut, ReceivedCoin[] receiverCoins)
    {
        return receiverCoins.Any(receiverCoin => receiverCoin.OutPoint == prevOut);
    }

    public static void ClearPartialSignatures(PSBT proposalPsbt)
    {
        foreach (var input in proposalPsbt.Inputs)
        {
            input.PartialSigs.Clear();
        }
    }

    public static void ClearHdKeyPaths(PSBT proposalPsbt)
    {
        foreach (var input in proposalPsbt.Inputs)
        {
            input.HDKeyPaths.Clear();
        }

        foreach (var output in proposalPsbt.Outputs)
        {
            output.HDKeyPaths.Clear();
        }
    }
}
