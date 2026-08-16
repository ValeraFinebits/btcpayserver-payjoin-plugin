using BTCPayServer.Abstractions;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Payjoin.Data;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer;
using NBXplorer.Models;
using Payjoin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal sealed record PayjoinSenderStartResult(
    bool Success,
    string? SenderSessionId,
    string? OriginalTransactionId,
    string? PendingTransactionId,
    string? Error)
{
    /// <summary>The wallet signed on the server; the poller drives the rest.</summary>
    public static PayjoinSenderStartResult Started(string senderSessionId, string originalTransactionId) =>
        new(true, senderSessionId, originalTransactionId, null, null);

    /// <summary>
    /// The wallet cannot sign on the server. The transaction waits in BTCPay's pending
    /// transactions until the operator signs it, and only then does the payjoin session begin.
    /// </summary>
    public static PayjoinSenderStartResult AwaitingSignature(string senderSessionId, string originalTransactionId, string pendingTransactionId) =>
        new(true, senderSessionId, originalTransactionId, pendingTransactionId, null);

    public static PayjoinSenderStartResult Failed(string error) => new(false, null, null, null, error);
}

/// <summary>
/// Starts sender payjoin sessions from a BIP 21 URI: parses the URI through rust-payjoin, builds
/// the original transaction from the store's wallet, hands it to the library's sender state
/// machine, and persists the session for the poller to drive. A hot wallet signs here. Any other
/// wallet signs through BTCPay's pending transactions, and the session waits until it does. The
/// original transaction never touches the network here; it is the fallback the poller broadcasts
/// when the payjoin does not complete.
/// </summary>
internal sealed class PayjoinSenderService
{
    // Relay floor for the payjoin round, expressed the way the library wants it:
    // 250 sat/kWU equals 1 sat/vB.
    internal const ulong MinFeeRateSatPerKwu = 250;

    /// <summary>
    /// The fee rate this sender gives rust-payjoin. The library uses it twice: it is the floor
    /// the receiver's proposal must clear, and it sizes the fee this sender contributes for the
    /// receiver's extra input. Both must reflect the rate the operator chose, or the receiver can
    /// hand back a proposal that confirms far slower than the operator asked for. Sessions from
    /// before this was recorded fall back to the relay floor.
    /// </summary>
    internal static ulong ResolveMinFeeRate(long feeRateSatPerKwu) =>
        feeRateSatPerKwu <= 0 ? MinFeeRateSatPerKwu : Math.Max(MinFeeRateSatPerKwu, (ulong)feeRateSatPerKwu);

    /// <summary>
    /// NBitcoin counts fee rates in satoshi per virtual byte; rust-payjoin counts them in
    /// satoshi per thousand weight units, and a virtual byte is four weight units.
    /// </summary>
    internal static long ToSatPerKwu(FeeRate feeRate) =>
        checked((long)(feeRate.SatoshiPerByte * 250m));

    // How long a transaction waits for an off-server signature before BTCPay retires it. The
    // coins stay reserved for that long, so this bounds how long a forgotten signing request
    // holds them. TODO: consider making this a store setting.
    private static readonly TimeSpan SignatureWindow = TimeSpan.FromDays(7);

    private sealed record ServerSigner(ExtKey AccountKey, RootedKeyPath RootedKeyPath);

    private static readonly Action<ILogger, string, string, Exception?> LogSenderSessionStarted =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(1, nameof(LogSenderSessionStarted)),
            "Payjoin sender session {SenderSessionId} started for store {StoreId}");
    private static readonly Action<ILogger, string, string, Exception?> LogSenderSessionAwaitingSignature =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(2, nameof(LogSenderSessionAwaitingSignature)),
            "Payjoin sender session {SenderSessionId} waits for a signature on pending transaction {PendingTransactionId}");

    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly StoreRepository _storeRepository;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly ExplorerClientProvider _explorerClientProvider;
    private readonly IFeeProviderFactory _feeProviderFactory;
    private readonly PayjoinSenderSessionStore _senderSessionStore;
    private readonly PendingTransactionService _pendingTransactionService;
    private readonly ILogger<PayjoinSenderService> _logger;

    internal PayjoinSenderService(
        BTCPayNetworkProvider networkProvider,
        StoreRepository storeRepository,
        PaymentMethodHandlerDictionary handlers,
        ExplorerClientProvider explorerClientProvider,
        IFeeProviderFactory feeProviderFactory,
        PayjoinSenderSessionStore senderSessionStore,
        PendingTransactionService pendingTransactionService,
        ILogger<PayjoinSenderService> logger)
    {
        _networkProvider = networkProvider;
        _storeRepository = storeRepository;
        _handlers = handlers;
        _explorerClientProvider = explorerClientProvider;
        _feeProviderFactory = feeProviderFactory;
        _senderSessionStore = senderSessionStore;
        _pendingTransactionService = pendingTransactionService;
        _logger = logger;
    }

    /// <param name="selectedInputs">
    /// The coins the operator picked, when they came through BTCPay's own send screen with coin
    /// selection open. Empty means the wallet chooses.
    /// </param>
    public async Task<PayjoinSenderStartResult> StartAsync(
        string storeId,
        string bip21,
        decimal? feeRateSatPerVb,
        RequestBaseUrl requestBaseUrl,
        IReadOnlyCollection<string>? selectedInputs,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(bip21))
        {
            return PayjoinSenderStartResult.Failed("A BIP 21 payment URI is required.");
        }

        var network = _networkProvider.GetNetwork<BTCPayNetwork>(PayjoinConstants.BitcoinCode);
        if (network is null)
        {
            return PayjoinSenderStartResult.Failed("The BTC network is not available.");
        }

        // Parse and validate through the library, so URI-format knowledge stays in one place.
        // The whole flow runs inside the URI's disposal scope because the sender builder at
        // the end still needs the parsed PjUri.
        try
        {
            using var uri = global::Payjoin.Uri.Parse(bip21.Trim());
            using var pjUri = uri.CheckPjSupported();
            return await StartWithPjUriAsync(storeId, bip21, pjUri, network, feeRateSatPerVb, requestBaseUrl, selectedInputs, cancellationToken).ConfigureAwait(false);
        }
        catch (UriParseException ex)
        {
            return PayjoinSenderStartResult.Failed($"The payment URI is invalid: {ex.Message}");
        }
        catch (PjNotSupported)
        {
            return PayjoinSenderStartResult.Failed("The URI does not advertise payjoin support.");
        }
    }

    private async Task<PayjoinSenderStartResult> StartWithPjUriAsync(
        string storeId,
        string bip21,
        PjUri pjUri,
        BTCPayNetwork network,
        decimal? feeRateSatPerVb,
        RequestBaseUrl requestBaseUrl,
        IReadOnlyCollection<string>? selectedInputs,
        CancellationToken cancellationToken)
    {
        var destinationAddress = pjUri.Address();
        var amountSats = pjUri.AmountSats();
        if (amountSats is null or 0)
        {
            return PayjoinSenderStartResult.Failed("The URI carries no amount; payjoin sending requires one.");
        }

        // Check the URI before building anything. Two attempts on one URI can select different
        // coins, so the transaction id below does not catch a repeated submission on its own.
        if (_senderSessionStore.HasPendingSessionForBip21(bip21.Trim()))
        {
            return PayjoinSenderStartResult.Failed("A payjoin session already pays this URI.");
        }

        var store = await _storeRepository.FindStore(storeId).ConfigureAwait(false);
        if (store is null)
        {
            return PayjoinSenderStartResult.Failed("The store was not found.");
        }

        var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(PayjoinConstants.BitcoinCode);
        var derivationScheme = store.GetPaymentMethodConfig<DerivationSchemeSettings>(paymentMethodId, _handlers, true);
        if (derivationScheme is null)
        {
            return PayjoinSenderStartResult.Failed("The store has no BTC wallet.");
        }

        var explorerClient = _explorerClientProvider.GetExplorerClient(network);
        var feeRate = feeRateSatPerVb is decimal explicitRate and > 0m
            ? new FeeRate(explicitRate)
            : await _feeProviderFactory.CreateFeeProvider(network).GetFeeRateAsync().ConfigureAwait(false);

        var psbtResponse = await explorerClient.CreatePSBTAsync(
            derivationScheme.AccountDerivation,
            new CreatePSBTRequest
            {
                RBF = network.SupportRBF ? true : null,
                // The full previous transaction authenticates each spent output; a bare
                // witness_utxo is counterparty-assertable data, so always carry the
                // authenticated form in the original we hand to the receiver.
                AlwaysIncludeNonWitnessUTXO = true,
                Destinations =
                {
                    new CreatePSBTDestination
                    {
                        Destination = BitcoinAddress.Create(destinationAddress, network.NBitcoinNetwork),
                        Amount = Money.Satoshis(checked((long)amountSats.Value))
                    }
                },
                FeePreference = new FeePreference { ExplicitFeeRate = feeRate },
                // Coin selection from BTCPay's send screen, when the operator opened it.
                IncludeOnlyOutpoints = selectedInputs is { Count: > 0 }
                    ? selectedInputs.Select(NBitcoin.OutPoint.Parse).ToList()
                    : null,
                // Coins already committed are not available: a pending transaction holds some,
                // and a live payjoin session holds the rest. Core's own send flow applies the
                // same exclusion for pending transactions, so the two cannot pick one UTXO twice.
                ExcludeOutpoints = await GetCommittedOutpointsAsync(storeId, network).ConfigureAwait(false)
            },
            cancellationToken).ConfigureAwait(false);
        if (psbtResponse is null)
        {
            return PayjoinSenderStartResult.Failed("The wallet could not create the transaction.");
        }

        var psbt = psbtResponse.PSBT;

        // Identify the transaction by its unsigned txid. It is known before signing, which the
        // off-server path needs, and it matches what BTCPay records for a pending transaction.
        // Signing does not move it for the segwit inputs this flow sends; a legacy input would
        // move it, and such an input also breaks payjoin txid stability in general.
        var originalTransactionId = psbt.GetGlobalTransaction().GetHash().ToString();
        var feeRateSatPerKwu = ToSatPerKwu(feeRate);
        var outpointsUsed = psbt.Inputs.Select(x => x.PrevOut.ToString()).ToArray();
        // Coins are the second axis: a different URI must not spend what a live session already
        // committed. Pending transactions are excluded above, and this covers the rest.
        if (_senderSessionStore.HasPendingSessionForOriginal(originalTransactionId))
        {
            return PayjoinSenderStartResult.Failed("A payjoin session already pays this transaction.");
        }

        var senderSessionId = Guid.NewGuid().ToString("N");
        var signer = await TryResolveServerSignerAsync(derivationScheme, network, cancellationToken).ConfigureAwait(false);
        if (signer is null)
        {
            // No key on the server: hand the transaction to BTCPay's pending transactions, where
            // the vault, a hardware device, a seed or a multisig group can sign it. Nothing goes
            // to the directory until that signature arrives.
            var pending = await _pendingTransactionService.CreatePendingTransaction(
                storeId,
                PayjoinConstants.BitcoinCode,
                psbt,
                requestBaseUrl,
                expiry: DateTimeOffset.UtcNow + SignatureWindow,
                cancellationToken).ConfigureAwait(false);

            _senderSessionStore.CreateSession(
                senderSessionId,
                storeId,
                bip21.Trim(),
                destinationAddress,
                checked((long)amountSats.Value),
                originalTransactionId,
                [],
                feeRateSatPerKwu,
                outpointsUsed,
                // The original is not signed yet, so the session has no fallback to offer until
                // the operator signs it.
                originalTransactionHex: null,
                pending.Id,
                PayjoinSenderSessionStatus.AwaitingSignature,
                requestBaseUrl.ToString());

            LogSenderSessionAwaitingSignature(_logger, senderSessionId, pending.Id, null);
            return PayjoinSenderStartResult.AwaitingSignature(senderSessionId, originalTransactionId, pending.Id);
        }

        psbt = psbt.SignAll(derivationScheme.AccountDerivation, signer.AccountKey, signer.RootedKeyPath);
        if (!psbt.TryFinalize(out var finalizeErrors))
        {
            return PayjoinSenderStartResult.Failed($"The original transaction could not be finalized: {string.Join("; ", finalizeErrors.Select(e => e.ToString()))}");
        }

        var bootstrapPersister = new CapturingSenderSessionPersister();
        try
        {
            using var senderBuilder = new SenderBuilder(psbt.ToBase64(), pjUri);
            using var transition = senderBuilder.BuildRecommended(ResolveMinFeeRate(feeRateSatPerKwu));
            using var sender = transition.Save(bootstrapPersister);
        }
        catch (UniffiException ex)
        {
            return PayjoinSenderStartResult.Failed($"The payjoin sender could not be created: {ex.Message}");
        }

        _senderSessionStore.CreateSession(
            senderSessionId,
            storeId,
            bip21.Trim(),
            destinationAddress,
            checked((long)amountSats.Value),
            originalTransactionId,
            bootstrapPersister.Load(),
            feeRateSatPerKwu,
            outpointsUsed,
            psbt.ExtractTransaction().ToHex());

        LogSenderSessionStarted(_logger, senderSessionId, storeId, null);
        return PayjoinSenderStartResult.Started(senderSessionId, originalTransactionId);
    }

    /// <summary>
    /// Returns the account key when the server holds one, and null when it does not. A null
    /// answer is the normal case for a cold wallet, a hardware device or a multisig group, and
    /// it routes the transaction to BTCPay's pending transactions instead.
    /// </summary>
    private async Task<ServerSigner?> TryResolveServerSignerAsync(
        DerivationSchemeSettings derivationScheme,
        BTCPayNetwork network,
        CancellationToken cancellationToken)
    {
        if (!derivationScheme.IsHotWallet)
        {
            return null;
        }

        var explorerClient = _explorerClientProvider.GetExplorerClient(network);
        var signingKeyStr = await explorerClient.GetMetadataAsync<string>(
            derivationScheme.AccountDerivation,
            WellknownMetadataKeys.MasterHDKey,
            cancellationToken).ConfigureAwait(false);
        if (signingKeyStr is null)
        {
            return null;
        }

        var signingKey = ExtKey.Parse(signingKeyStr, network.NBitcoinNetwork);
        var rootedKeyPath = derivationScheme.GetAccountKeySettingsFromRoot(signingKey)?.GetRootedKeyPath();
        if (rootedKeyPath is null)
        {
            return null;
        }

        return new ServerSigner(signingKey.Derive(rootedKeyPath.KeyPath), rootedKeyPath);
    }

    private async Task<List<NBitcoin.OutPoint>> GetCommittedOutpointsAsync(string storeId, BTCPayNetwork network)
    {
        var pending = await _pendingTransactionService
            .GetPendingTransactions(network.CryptoCode, storeId)
            .ConfigureAwait(false);
        return pending.SelectMany(x => x.OutpointsUsed)
            .Concat(_senderSessionStore.GetOutpointsHeldByLiveSessions(storeId))
            .Distinct(StringComparer.Ordinal)
            .Select(NBitcoin.OutPoint.Parse)
            .ToList();
    }
}
