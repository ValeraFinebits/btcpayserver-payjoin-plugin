using BTCPayServer.Services.Wallets;
using NBitcoin;
using NBXplorer;
using NBXplorer.Models;
using Payjoin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using static Payjoin.SenderPersistedException;
using PayjoinUri = Payjoin.Uri;
using SystemUri = System.Uri;

namespace BTCPayServer.Plugins.Payjoin.Services;

public interface IRunTestPaymentService
{
    Task<string> ExecuteAsync(RunTestPaymentContext runTestPaymentContext, CancellationToken cancellationToken);
}

// TODO: Remove this test service
public sealed class RunTestPaymentService : IRunTestPaymentService
{
    private const int RecommendedFeeContributionRate = 250;
    private const int MaxProposalPollAttempts = 5;
    private const decimal ExplicitFeeRateSatoshiPerByte = 1.0m;
    private static readonly TimeSpan ProposalPollDelay = TimeSpan.FromMilliseconds(250);
    private static readonly Money FeeBuffer = Money.Satoshis(1000);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ExplorerClientProvider _explorerClientProvider;
    private readonly BTCPayWalletProvider _walletProvider;

    public RunTestPaymentService(
        IHttpClientFactory httpClientFactory,
        ExplorerClientProvider explorerClientProvider,
        BTCPayWalletProvider walletProvider)
    {
        _httpClientFactory = httpClientFactory;
        _explorerClientProvider = explorerClientProvider;
        _walletProvider = walletProvider;
    }

    public async Task<string> ExecuteAsync(RunTestPaymentContext runTestPaymentContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runTestPaymentContext);

        var senderPsbtResult = await CreateSenderPsbtAsync(runTestPaymentContext, cancellationToken).ConfigureAwait(false);
        var baseline = SelfPayInvariantChecker.CreateBaseline(senderPsbtResult.Psbt.GetGlobalTransaction(), runTestPaymentContext.PaymentAddress.ScriptPubKey, senderPsbtResult.AmountToSend);
        var proposalPsbt = await RequestProposalAsync(runTestPaymentContext.PaymentUrl, runTestPaymentContext.OhttpRelayUrl, senderPsbtResult.Psbt, runTestPaymentContext.Network, cancellationToken).ConfigureAwait(false);
        SelfPayInvariantChecker.ValidateProposal(proposalPsbt.GetGlobalTransaction(), baseline);

        return await SignAndBroadcastAsync(runTestPaymentContext, proposalPsbt, baseline, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CreateSenderPsbtResult> CreateSenderPsbtAsync(RunTestPaymentContext runTestPaymentContext, CancellationToken cancellationToken)
    {
        var wallet = _walletProvider.GetWallet(runTestPaymentContext.Network);
        if (wallet is null)
        {
            throw new RunTestPaymentExecutionException("wallet not available");
        }

        var confirmedCoins = await wallet.GetUnspentCoins(runTestPaymentContext.DerivationScheme.AccountDerivation, true, cancellationToken).ConfigureAwait(false);
        var allCoins = await wallet.GetUnspentCoins(runTestPaymentContext.DerivationScheme.AccountDerivation, false, cancellationToken).ConfigureAwait(false);
        var confirmedTotal = confirmedCoins.Sum(coin => coin.Value.GetValue(runTestPaymentContext.Network));
        var allTotal = allCoins.Sum(coin => coin.Value.GetValue(runTestPaymentContext.Network));

        var psbtRequest = new CreatePSBTRequest
        {
            RBF = runTestPaymentContext.Network.SupportRBF ? true : null,
            ReserveChangeAddress = true,
            FeePreference = new FeePreference
            {
                ExplicitFeeRate = new FeeRate(ExplicitFeeRateSatoshiPerByte)
            }
        };

        var available = confirmedTotal < runTestPaymentContext.PaymentAmount && allTotal >= runTestPaymentContext.PaymentAmount ? allTotal : confirmedTotal;
        var feeBuffer = FeeBuffer.ToDecimal(MoneyUnit.BTC);
        var spendable = Math.Max(available - feeBuffer, 0.0m);
        if (spendable <= 0.0m)
        {
            throw new RunTestPaymentExecutionException("wallet funds too low for fees");
        }

        var amountToSend = Money.Coins(Math.Min(runTestPaymentContext.PaymentAmount, spendable));
        psbtRequest.Destinations.Add(new CreatePSBTDestination
        {
            Destination = runTestPaymentContext.PaymentAddress,
            Amount = amountToSend
        });

        if (confirmedTotal < runTestPaymentContext.PaymentAmount && allTotal >= runTestPaymentContext.PaymentAmount && allCoins.Length > 0)
        {
            psbtRequest.IncludeOnlyOutpoints = allCoins.Select(coin => coin.OutPoint).ToList();
        }

        var client = _explorerClientProvider.GetExplorerClient(runTestPaymentContext.Network);
        try
        {
            var createResult = await client.CreatePSBTAsync(runTestPaymentContext.DerivationScheme.AccountDerivation, psbtRequest, cancellationToken).ConfigureAwait(false);
            if (createResult?.PSBT is null)
            {
                throw new RunTestPaymentExecutionException("psbt creation failed");
            }

            return new CreateSenderPsbtResult(createResult.PSBT, amountToSend);
        }
        catch (NBXplorerException ex)
        {
            throw new RunTestPaymentExecutionException(ex.Message, ex);
        }
    }

    private async Task<PSBT> RequestProposalAsync(SystemUri paymentUrl, SystemUri ohttpRelayUrl, PSBT senderPsbt, BTCPayNetwork network, CancellationToken cancellationToken)
    {
        string? proposalPsbtBase64 = null;

        using var parsedUri = PayjoinUri.Parse(paymentUrl.ToString());
        using var pjUri = parsedUri.CheckPjSupported();
        try
        {
            var senderPersister = new InMemorySenderPersister();
            using var senderBuilder = new SenderBuilder(senderPsbt.ToBase64(), pjUri);
            using var initial = senderBuilder.BuildRecommended(RecommendedFeeContributionRate);
            using var withReplyKey = initial.Save(senderPersister);

            using var postContext = withReplyKey.CreateV2PostRequest(ohttpRelayUrl.ToString());
            var postResponse = await SendRequestAsync(postContext.request, cancellationToken).ConfigureAwait(false);
            using var withReplyTransition = withReplyKey.ProcessResponse(postResponse, postContext.ohttpCtx);

            var current = withReplyTransition.Save(senderPersister);
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
        }
        catch (BuildSenderException ex)
        {
            throw new RunTestPaymentExecutionException($"Sender build failed: {ex.Message}", ex);
        }
        catch (SenderPersistedException.ResponseException ex)
        {
            throw new RunTestPaymentExecutionException($"Sender rejected by receiver: {FormatSenderResponseException(ex.v1)}", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new RunTestPaymentExecutionException($"Sender failed: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new RunTestPaymentExecutionException($"Sender failed: {ex.Message}", ex);
        }
        catch (SenderPersistedException ex)
        {
            throw new RunTestPaymentExecutionException($"Sender state failed: {ex.Message}", ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new RunTestPaymentExecutionException($"Sender failed: {ex.Message}", ex);
        }

        if (string.IsNullOrWhiteSpace(proposalPsbtBase64))
        {
            throw new RunTestPaymentExecutionException("No payjoin proposal yet");
        }

        try
        {
            return PSBT.Parse(proposalPsbtBase64, network.NBitcoinNetwork);
        }
        catch (FormatException ex)
        {
            throw new RunTestPaymentExecutionException($"Invalid PSBT: {ex.Message}", ex);
        }
        catch (ArgumentException ex)
        {
            throw new RunTestPaymentExecutionException($"Invalid PSBT: {ex.Message}", ex);
        }
    }

    private async Task<string> SignAndBroadcastAsync(RunTestPaymentContext runTestPaymentContext, PSBT proposalPsbt, SelfPayInvariantChecker.SelfPayBaseline baseline, CancellationToken cancellationToken)
    {
        var client = _explorerClientProvider.GetExplorerClient(runTestPaymentContext.Network);
        runTestPaymentContext.DerivationScheme.RebaseKeyPaths(proposalPsbt);
        var signingKeyStr = await client.GetMetadataAsync<string>(
            runTestPaymentContext.DerivationScheme.AccountDerivation,
            WellknownMetadataKeys.MasterHDKey,
            cancellationToken).ConfigureAwait(false);
        if (!runTestPaymentContext.DerivationScheme.IsHotWallet || signingKeyStr is null)
        {
            var error = !runTestPaymentContext.DerivationScheme.IsHotWallet
                ? "cannot sign from a cold wallet"
                : "wallet seed not available";
            throw new RunTestPaymentExecutionException(error);
        }

        var signingKey = ExtKey.Parse(signingKeyStr, runTestPaymentContext.Network.NBitcoinNetwork);
        var signingKeySettings = runTestPaymentContext.DerivationScheme.GetAccountKeySettingsFromRoot(signingKey);
        var rootedKeyPath = signingKeySettings?.GetRootedKeyPath();
        if (rootedKeyPath is null || signingKeySettings is null)
        {
            throw new RunTestPaymentExecutionException("wallet key path mismatch");
        }

        proposalPsbt.RebaseKeyPaths(signingKeySettings.AccountKey, rootedKeyPath);
        var accountKey = signingKey.Derive(rootedKeyPath.KeyPath);
        proposalPsbt.SignAll(runTestPaymentContext.DerivationScheme.AccountDerivation, accountKey, rootedKeyPath);

        if (!proposalPsbt.TryFinalize(out _))
        {
            throw new RunTestPaymentExecutionException("PSBT could not be finalized");
        }

        var transaction = proposalPsbt.ExtractTransaction();
        SelfPayInvariantChecker.ValidateFinalTransaction(transaction, baseline);

        var broadcast = await client.BroadcastAsync(transaction, cancellationToken).ConfigureAwait(false);
        if (!broadcast.Success)
        {
            throw new RunTestPaymentExecutionException($"Broadcast failed: {broadcast.RPCCode} {broadcast.RPCCodeMessage} {broadcast.RPCMessage}");
        }

        var rpc = client.RPCClient;
        if (rpc is not null)
        {
            var rewardAddress = await rpc.GetNewAddressAsync(cancellationToken).ConfigureAwait(false);
            await rpc.GenerateToAddressAsync(1, rewardAddress, cancellationToken).ConfigureAwait(false);
        }

        return transaction.GetHash().ToString();
    }

    private async Task<byte[]> SendRequestAsync(Request request, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, request.url)
        {
            Content = new ByteArrayContent(request.body)
        };
        message.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(request.contentType);

        var client = _httpClientFactory.CreateClient(nameof(RunTestPaymentService));
        using var response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
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

    internal sealed class RunTestPaymentExecutionException : InvalidOperationException
    {
        public RunTestPaymentExecutionException()
        {
        }

        public RunTestPaymentExecutionException(string message) : base(message)
        {
        }

        public RunTestPaymentExecutionException(string message, Exception innerException) : base(message, innerException)
        {
        }
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

    private sealed record CreateSenderPsbtResult(PSBT Psbt, Money AmountToSend);
}

public sealed record RunTestPaymentContext(
    string InvoiceId,
    SystemUri PaymentUrl,
    SystemUri OhttpRelayUrl,
    BitcoinAddress PaymentAddress,
    decimal PaymentAmount,
    BTCPayNetwork Network,
    DerivationSchemeSettings DerivationScheme);
