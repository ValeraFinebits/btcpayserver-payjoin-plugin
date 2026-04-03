using BTCPayServer.Services.Wallets;
using BTCPayServer.Tests;
using NBitcoin;
using NBXplorer;
using NBXplorer.Models;
using Payjoin;
using System.Text;
using PayjoinUri = Payjoin.Uri;
using SystemUri = System.Uri;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;

internal sealed class PayjoinTestPayer
{
    private static readonly TimeSpan ProposalPollDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RelayRequestTimeout = TimeSpan.FromSeconds(10);
    private const int MaxProposalPollAttempts = 5;
    private const int RecommendedFeeContributionRate = 250;
    private const decimal ExplicitFeeRateSatoshiPerByte = 1.0m;
    private static readonly Money FeeBuffer = Money.Satoshis(1000);

    private readonly ServerTester _tester;
    private readonly TestAccount _payer;
    private readonly BTCPayNetwork _network;
    private readonly ExplorerClient _explorerClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly BTCPayWalletProvider _walletProvider;

    public PayjoinTestPayer(ServerTester tester, TestAccount payer, BTCPayNetwork network)
    {
        ArgumentNullException.ThrowIfNull(tester);
        ArgumentNullException.ThrowIfNull(payer);
        ArgumentNullException.ThrowIfNull(network);

        _tester = tester;
        _payer = payer;
        _network = network;
        _explorerClient = tester.PayTester.GetService<ExplorerClientProvider>().GetExplorerClient(network);
        _httpClientFactory = tester.PayTester.GetService<IHttpClientFactory>();
        _walletProvider = tester.PayTester.GetService<BTCPayWalletProvider>();
    }

    public async Task<PayjoinTestPaymentResult> PayAsync(SystemUri paymentUrl, SystemUri ohttpRelayUrl, CancellationToken cancellationToken)
    {
        return await PayAsync(paymentUrl, ohttpRelayUrl, preProposalPollDelay: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PayjoinTestPaymentResult> PayAsync(SystemUri paymentUrl, SystemUri ohttpRelayUrl, TimeSpan? preProposalPollDelay, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paymentUrl);
        ArgumentNullException.ThrowIfNull(ohttpRelayUrl);

        var senderPsbt = await CreateSenderPsbtAsync(paymentUrl, cancellationToken).ConfigureAwait(false);
        var proposalPsbt = await RequestProposalAsync(paymentUrl, ohttpRelayUrl, senderPsbt, preProposalPollDelay, cancellationToken).ConfigureAwait(false);

        return await FinalizeAndBroadcastAsync(proposalPsbt, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PayjoinTestPaymentResult> FinalizeAndBroadcastAsync(PSBT proposalPsbt, CancellationToken cancellationToken)
    {
        var signedProposal = await _payer.Sign(proposalPsbt).ConfigureAwait(false);

        if (!signedProposal.TryFinalize(out _))
        {
            throw new InvalidOperationException($"PSBT could not be finalized for payer store '{_payer.StoreId}'.");
        }

        var transaction = signedProposal.ExtractTransaction();
        await BroadcastTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
        await MineBlockAsync(cancellationToken).ConfigureAwait(false);

        return new PayjoinTestPaymentResult(transaction.GetHash().ToString());
    }

    private async Task BroadcastTransactionAsync(Transaction transaction, CancellationToken cancellationToken)
    {
        var broadcast = await _explorerClient.BroadcastAsync(transaction, cancellationToken).ConfigureAwait(false);
        if (!broadcast.Success)
        {
            throw new InvalidOperationException($"Broadcast failed for payer store '{_payer.StoreId}': {broadcast.RPCCode} {broadcast.RPCCodeMessage} {broadcast.RPCMessage}");
        }
    }

    private async Task<PSBT> CreateSenderPsbtAsync(SystemUri paymentUrl, CancellationToken cancellationToken)
    {
        string paymentAddress;
        decimal paymentAmount;
        try
        {
            using var parsedUri = PayjoinUri.Parse(paymentUrl.ToString());
            paymentAddress = parsedUri.Address();
            var amountSats = parsedUri.AmountSats();
            if (amountSats is null)
            {
                throw new InvalidOperationException("payment amount missing in paymentUrl");
            }

            paymentAmount = Money.Satoshis(checked((long)amountSats.Value)).ToDecimal(MoneyUnit.BTC);
            using var _ = parsedUri.CheckPjSupported();
        }
        catch (PjParseException ex)
        {
            throw new InvalidOperationException($"Invalid BIP21 URI '{paymentUrl}': {ex.Message}", ex);
        }
        catch (PjNotSupported ex)
        {
            throw new InvalidOperationException($"Payjoin not available in URI '{paymentUrl}': {ex.Message}", ex);
        }

        var wallet = _walletProvider.GetWallet(_network)
            ?? throw new InvalidOperationException("wallet not available");
        var confirmedCoins = await wallet.GetUnspentCoins(_payer.DerivationScheme, true, cancellationToken).ConfigureAwait(false);
        var allCoins = await wallet.GetUnspentCoins(_payer.DerivationScheme, false, cancellationToken).ConfigureAwait(false);
        var confirmedTotal = confirmedCoins.Sum(coin => coin.Value.GetValue(_network));
        var allTotal = allCoins.Sum(coin => coin.Value.GetValue(_network));

        var psbtRequest = new CreatePSBTRequest
        {
            RBF = _network.SupportRBF ? true : null,
            FeePreference = new FeePreference
            {
                ExplicitFeeRate = new FeeRate(ExplicitFeeRateSatoshiPerByte)
            }
        };

        var available = confirmedTotal < paymentAmount && allTotal >= paymentAmount ? allTotal : confirmedTotal;
        var feeBuffer = FeeBuffer.ToDecimal(MoneyUnit.BTC);
        var spendable = Math.Max(available - feeBuffer, 0.0m);
        if (spendable <= 0.0m)
        {
            throw new InvalidOperationException($"Payer store '{_payer.StoreId}' has no spendable funds after reserving a {FeeBuffer.Satoshi} sat fee buffer. Confirmed={confirmedTotal:0.########}, Total={allTotal:0.########}, Required={paymentAmount:0.########} BTC.");
        }

        if (spendable < paymentAmount)
        {
            throw new InvalidOperationException($"Payer store '{_payer.StoreId}' does not have enough spendable BTC for the invoice amount. Spendable={spendable:0.########}, Required={paymentAmount:0.########}, Confirmed={confirmedTotal:0.########}, Total={allTotal:0.########}, FeeBufferSats={FeeBuffer.Satoshi}.");
        }

        psbtRequest.Destinations.Add(new CreatePSBTDestination
        {
            Destination = BitcoinAddress.Create(paymentAddress, _network.NBitcoinNetwork),
            Amount = Money.Coins(paymentAmount)
        });

        if (confirmedTotal < paymentAmount && allTotal >= paymentAmount && allCoins.Length > 0)
        {
            psbtRequest.IncludeOnlyOutpoints = allCoins.Select(coin => coin.OutPoint).ToList();
        }

        CreatePSBTResponse? createResult;
        try
        {
            createResult = await _explorerClient.CreatePSBTAsync(_payer.DerivationScheme, psbtRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (NBXplorerException ex)
        {
            throw new InvalidOperationException($"Failed to create sender PSBT for payer store '{_payer.StoreId}': {ex.Message}", ex);
        }

        if (createResult?.PSBT is null)
        {
            throw new InvalidOperationException($"Sender PSBT creation returned no PSBT for payer store '{_payer.StoreId}'.");
        }

        return createResult.PSBT;
    }

    private async Task<PSBT> RequestProposalAsync(SystemUri paymentUrl, SystemUri ohttpRelayUrl, PSBT senderPsbt, TimeSpan? preProposalPollDelay, CancellationToken cancellationToken)
    {
        string? proposalPsbtBase64 = null;
        var senderPersister = new InMemorySenderPersister();
        using var parsedUri = PayjoinUri.Parse(paymentUrl.ToString());
        using var pjUri = parsedUri.CheckPjSupported();
        using var senderBuilder = new SenderBuilder(senderPsbt.ToBase64(), pjUri);
        using var initial = senderBuilder.BuildRecommended(RecommendedFeeContributionRate);
        using var withReplyKey = initial.Save(senderPersister);

        using var postContext = withReplyKey.CreateV2PostRequest(ohttpRelayUrl.ToString());
        var postResponse = await SendRequestAsync(postContext.request, cancellationToken).ConfigureAwait(false);
        using var withReplyTransition = withReplyKey.ProcessResponse(postResponse, postContext.ohttpCtx);

        try
        {
            var current = withReplyTransition.Save(senderPersister);
            if (preProposalPollDelay is { } delay && delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            proposalPsbtBase64 = await PollForProposalAsync(current, senderPersister, ohttpRelayUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (SenderPersistedException.ResponseException ex)
        {
            throw CreateSenderResponseFailure(ex);
        }

        if (string.IsNullOrWhiteSpace(proposalPsbtBase64))
        {
            throw new InvalidOperationException($"No payjoin proposal was received after {MaxProposalPollAttempts} poll attempts for payer store '{_payer.StoreId}' via relay '{ohttpRelayUrl}'.");
        }

        try
        {
            return PSBT.Parse(proposalPsbtBase64, _network.NBitcoinNetwork);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"Invalid payjoin proposal PSBT for payer store '{_payer.StoreId}': {ex.Message}", ex);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"Invalid payjoin proposal PSBT for payer store '{_payer.StoreId}': {ex.Message}", ex);
        }
    }

    private async Task<string?> PollForProposalAsync(
        PollingForProposal current,
        InMemorySenderPersister senderPersister,
        SystemUri ohttpRelayUrl,
        CancellationToken cancellationToken)
    {
        string? proposalPsbtBase64 = null;

        try
        {
            for (var attempt = 0; attempt < MaxProposalPollAttempts; attempt++)
            {
                using var pollRequest = current.CreatePollRequest(ohttpRelayUrl.ToString());
                var pollResponse = await SendRequestAsync(pollRequest.request, cancellationToken).ConfigureAwait(false);
                using var pollTransition = current.ProcessResponse(pollResponse, pollRequest.ohttpCtx);

                var outcome = pollTransition.Save(senderPersister);

                try
                {
                    switch (outcome)
                    {
                        case PollingForProposalTransitionOutcome.Progress progress:
                            proposalPsbtBase64 = progress.psbtBase64;
                            break;
                        case PollingForProposalTransitionOutcome.Stasis stasis:
                            current.Dispose();
                            current = stasis.inner;
                            outcome = null;
                            break;
                    }
                }
                finally
                {
                    outcome?.Dispose();
                }

                if (proposalPsbtBase64 is not null)
                {
                    break;
                }

                await Task.Delay(ProposalPollDelay, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            current.Dispose();
        }

        return proposalPsbtBase64;
    }

    private InvalidOperationException CreateSenderResponseFailure(SenderPersistedException.ResponseException ex)
    {
        return new InvalidOperationException(
            $"Payjoin receiver rejected the proposal for payer store '{_payer.StoreId}': {FormatSenderResponseException(ex.v1)}",
            ex);
    }

    private static string FormatSenderResponseException(global::Payjoin.ResponseException responseException)
    {
        return responseException switch
        {
            global::Payjoin.ResponseException.WellKnown wellKnown => wellKnown.v1.ToString() ?? nameof(global::Payjoin.ResponseException.WellKnown),
            global::Payjoin.ResponseException.Validation validation => validation.v1.ToString() ?? nameof(global::Payjoin.ResponseException.Validation),
            global::Payjoin.ResponseException.Unrecognized unrecognized => $"{unrecognized.errorCode}: {unrecognized.msg}",
            _ => responseException.ToString() ?? responseException.GetType().Name
        };
    }

    private async Task<byte[]> SendRequestAsync(Request request, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, request.url)
        {
            Content = new ByteArrayContent(request.body)
        };
        message.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(request.contentType);

        var client = _httpClientFactory.CreateClient(nameof(PayjoinTestPayer));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(RelayRequestTimeout);

        HttpResponseMessage response;
        byte[] responseBody;
        try
        {
            response = await client.SendAsync(message, timeoutCts.Token).ConfigureAwait(false);
            responseBody = await response.Content.ReadAsByteArrayAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Payjoin HTTP request timed out after {RelayRequestTimeout.TotalSeconds:0} seconds. RequestUrl='{request.url}', RequestContentType='{request.contentType}'.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw CreateHttpRequestFailure(request, response, responseBody);
            }

            return responseBody;
        }
    }

    private static InvalidOperationException CreateHttpRequestFailure(Request request, HttpResponseMessage response, byte[] responseBody)
    {
        var responseContentType = response.Content.Headers.ContentType?.ToString() ?? "<none>";
        var diagnosticBody = FormatResponseBody(responseBody, responseContentType);

        return new InvalidOperationException(
            $"Payjoin HTTP request failed. RequestUrl='{request.url}', RequestContentType='{request.contentType}', ResponseStatusCode={(int)response.StatusCode} ({response.StatusCode}), ResponseContentType='{responseContentType}', ResponseBody='{diagnosticBody}'.");
    }

    private static string FormatResponseBody(byte[] responseBody, string responseContentType)
    {
        if (responseBody.Length == 0)
        {
            return "<empty>";
        }

        const int maxBodyLength = 512;
        var bodyPrefix = responseBody.Length > maxBodyLength
            ? responseBody[..maxBodyLength]
            : responseBody;

        if (responseContentType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
            responseContentType.Contains("text", StringComparison.OrdinalIgnoreCase) ||
            responseContentType.Contains("xml", StringComparison.OrdinalIgnoreCase) ||
            responseContentType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            var text = Encoding.UTF8.GetString(bodyPrefix)
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal);
            return responseBody.Length > maxBodyLength ? $"{text}... [truncated]" : text;
        }

        var hex = Convert.ToHexString(bodyPrefix);
        return responseBody.Length > maxBodyLength ? $"{hex}... [truncated hex]" : hex;
    }

    private async Task MineBlockAsync(CancellationToken cancellationToken)
    {
        var rpc = _explorerClient.RPCClient ?? _tester.ExplorerNode;
        var rewardAddress = await rpc.GetNewAddressAsync(cancellationToken).ConfigureAwait(false);
        await rpc.GenerateToAddressAsync(1, rewardAddress, cancellationToken).ConfigureAwait(false);
    }

    private sealed class InMemorySenderPersister : JsonSenderSessionPersister
    {
        private readonly List<string> _events = new();

        public void Save(string @event) => _events.Add(@event);

        public string[] Load() => _events.ToArray();

        public void Close()
        {
        }
    }
}

internal sealed record PayjoinTestPaymentResult(string TransactionId);
