using BTCPayServer.Data;
using NBitcoin;
using NBXplorer;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

/// <summary>Shared signing-key resolution for the original and the receiver's proposal.</summary>
internal static class PayjoinSenderWallet
{
    internal sealed record Signer(ExtKey AccountKey, RootedKeyPath RootedKeyPath);

    // The HTTP entry point authorizes signing before starting an automatic payment. Background
    // work resumes that authorized payment; a missing server key takes the off-server path.
    internal static async Task<Signer?> ResolveSignerAsync(
        ExplorerClient explorerClient,
        DerivationSchemeSettings derivationScheme,
        BTCPayNetwork network,
        CancellationToken cancellationToken)
    {
        if (!derivationScheme.IsHotWallet)
        {
            return null;
        }

        var keyText = await explorerClient.GetMetadataAsync<string>(
            derivationScheme.AccountDerivation, WellknownMetadataKeys.MasterHDKey, cancellationToken).ConfigureAwait(false);
        if (keyText is null)
        {
            return null;
        }

        var key = ExtKey.Parse(keyText, network.NBitcoinNetwork);
        var path = derivationScheme.GetAccountKeySettingsFromRoot(key)?.GetRootedKeyPath();
        return path is null ? null : new Signer(key.Derive(path.KeyPath), path);
    }
}
