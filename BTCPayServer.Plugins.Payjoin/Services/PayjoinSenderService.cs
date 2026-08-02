using BTCPayServer.Data;
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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

internal sealed record PayjoinSenderStartResult(
    bool Success,
    string? SenderSessionId,
    string? OriginalTransactionId,
    string? Error)
{
    public static PayjoinSenderStartResult Started(string senderSessionId, string originalTransactionId) =>
        new(true, senderSessionId, originalTransactionId, null);

    public static PayjoinSenderStartResult Failed(string error) => new(false, null, null, error);
}

/// <summary>
/// Starts sender payjoin sessions from a BIP 21 URI: parses the URI through rust-payjoin, builds
/// and signs the original transaction from the store's hot wallet, hands it to the library's
/// sender state machine, and persists the session for the poller to drive. The original
/// transaction never touches the network here; it is the fallback the poller broadcasts when the
/// payjoin does not complete.
/// </summary>
internal sealed class PayjoinSenderService
{
    // Floor for the payjoin round, expressed the way the library wants it:
    // 250 sat/kWU equals 1 sat/vB.
    private const ulong MinFeeRateSatPerKwu = 250;

    private static readonly Action<ILogger, string, string, Exception?> LogSenderSessionStarted =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(1, nameof(LogSenderSessionStarted)),
            "Payjoin sender session {SenderSessionId} started for store {StoreId}");

    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly StoreRepository _storeRepository;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly ExplorerClientProvider _explorerClientProvider;
    private readonly IFeeProviderFactory _feeProviderFactory;
    private readonly PayjoinSenderSessionStore _senderSessionStore;
    private readonly ILogger<PayjoinSenderService> _logger;

    internal PayjoinSenderService(
        BTCPayNetworkProvider networkProvider,
        StoreRepository storeRepository,
        PaymentMethodHandlerDictionary handlers,
        ExplorerClientProvider explorerClientProvider,
        IFeeProviderFactory feeProviderFactory,
        PayjoinSenderSessionStore senderSessionStore,
        ILogger<PayjoinSenderService> logger)
    {
        _networkProvider = networkProvider;
        _storeRepository = storeRepository;
        _handlers = handlers;
        _explorerClientProvider = explorerClientProvider;
        _feeProviderFactory = feeProviderFactory;
        _senderSessionStore = senderSessionStore;
        _logger = logger;
    }

    public async Task<PayjoinSenderStartResult> StartAsync(
        string storeId,
        string bip21,
        decimal? feeRateSatPerVb,
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
            return await StartWithPjUriAsync(storeId, bip21, pjUri, network, feeRateSatPerVb, cancellationToken).ConfigureAwait(false);
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
        CancellationToken cancellationToken)
    {
        var destinationAddress = pjUri.Address();
        var amountSats = pjUri.AmountSats();
        if (amountSats is null or 0)
        {
            return PayjoinSenderStartResult.Failed("The URI carries no amount; payjoin sending requires one.");
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

        if (!derivationScheme.IsHotWallet)
        {
            return PayjoinSenderStartResult.Failed("Payjoin sending requires a hot wallet.");
        }

        var explorerClient = _explorerClientProvider.GetExplorerClient(network);
        var signingKeyStr = await explorerClient.GetMetadataAsync<string>(
            derivationScheme.AccountDerivation,
            WellknownMetadataKeys.MasterHDKey,
            cancellationToken).ConfigureAwait(false);
        if (signingKeyStr is null)
        {
            return PayjoinSenderStartResult.Failed("The wallet seed is not available.");
        }

        var signingKey = ExtKey.Parse(signingKeyStr, network.NBitcoinNetwork);
        var signingKeySettings = derivationScheme.GetAccountKeySettingsFromRoot(signingKey);
        var rootedKeyPath = signingKeySettings?.GetRootedKeyPath();
        if (signingKeySettings is null || rootedKeyPath is null)
        {
            return PayjoinSenderStartResult.Failed("The wallet key settings are not available.");
        }

        var accountKey = signingKey.Derive(rootedKeyPath.KeyPath);

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
                FeePreference = new FeePreference { ExplicitFeeRate = feeRate }
            },
            cancellationToken).ConfigureAwait(false);
        if (psbtResponse is null)
        {
            return PayjoinSenderStartResult.Failed("The wallet could not create the transaction.");
        }

        var psbt = psbtResponse.PSBT;
        psbt = psbt.SignAll(derivationScheme.AccountDerivation, accountKey, rootedKeyPath);
        if (!psbt.TryFinalize(out var finalizeErrors))
        {
            return PayjoinSenderStartResult.Failed($"The original transaction could not be finalized: {string.Join("; ", finalizeErrors.Select(e => e.ToString()))}");
        }

        var originalTransactionId = psbt.ExtractTransaction().GetHash().ToString();
        if (_senderSessionStore.HasPendingSessionForOriginal(originalTransactionId))
        {
            return PayjoinSenderStartResult.Failed("A pending payjoin session already pays this transaction.");
        }

        var senderSessionId = Guid.NewGuid().ToString("N");
        var bootstrapPersister = new CapturingSenderSessionPersister();
        try
        {
            using var senderBuilder = new SenderBuilder(psbt.ToBase64(), pjUri);
            using var transition = senderBuilder.BuildRecommended(MinFeeRateSatPerKwu);
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
            bootstrapPersister.Load());

        LogSenderSessionStarted(_logger, senderSessionId, storeId, null);
        return PayjoinSenderStartResult.Started(senderSessionId, originalTransactionId);
    }
}
