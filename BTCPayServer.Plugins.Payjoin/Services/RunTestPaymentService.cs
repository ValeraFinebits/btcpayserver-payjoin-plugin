using BTCPayServer.Services.Wallets;
using NBitcoin;
using NBXplorer.DerivationStrategy;
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
    private const decimal FundingBufferBtc = 0.01m;
    private static readonly TimeSpan ProposalPollDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan FundingPollDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan FundingConfirmationTimeout = TimeSpan.FromSeconds(30);
    private static readonly Money FeeBuffer = Money.Satoshis(1000);
    private static readonly KeyPath CheatModePayerAccountKeyPath = new("m/84'/1'/0'");

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

        var payer = await EnsureCheatModePayerAsync(runTestPaymentContext.Network, runTestPaymentContext.PaymentAmount, cancellationToken).ConfigureAwait(false);
        runTestPaymentContext = runTestPaymentContext with
        {
            PayerAccountDerivation = payer.AccountDerivation,
            PayerAccountKey = payer.AccountKey
        };

        var senderPsbtResult = await CreateSenderPsbtAsync(runTestPaymentContext, cancellationToken).ConfigureAwait(false);
        var proposalPsbt = await RequestProposalAsync(runTestPaymentContext.PaymentUrl, runTestPaymentContext.OhttpRelayUrl, senderPsbtResult.Psbt, runTestPaymentContext.Network, cancellationToken).ConfigureAwait(false);

        return await SignAndBroadcastAsync(runTestPaymentContext, proposalPsbt, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CheatModePayer> EnsureCheatModePayerAsync(BTCPayNetwork network, decimal paymentAmount, CancellationToken cancellationToken)
    {
        var payer = await CreateCheatModePayerAsync(network, cancellationToken).ConfigureAwait(false);
        await EnsureCheatModePayerFundsAsync(network, payer, paymentAmount, cancellationToken).ConfigureAwait(false);
        return payer;
    }

    private async Task<CheatModePayer> CreateCheatModePayerAsync(BTCPayNetwork network, CancellationToken cancellationToken)
    {
        var explorerClient = _explorerClientProvider.GetExplorerClient(network);
        var accountKey = new ExtKey().Derive(CheatModePayerAccountKeyPath);
        var derivationFactory = explorerClient.Network.DerivationStrategyFactory;
        var accountDerivation = derivationFactory.CreateDirectDerivationStrategy(
            accountKey.Neuter(),
            new DerivationStrategyOptions { ScriptPubKeyType = ScriptPubKeyType.Segwit });

        await explorerClient.TrackAsync(accountDerivation, cancellationToken).ConfigureAwait(false);

        return new CheatModePayer(accountDerivation, accountKey);
    }

    private async Task EnsureCheatModePayerFundsAsync(BTCPayNetwork network, CheatModePayer payer, decimal paymentAmount, CancellationToken cancellationToken)
    {
        var wallet = _walletProvider.GetWallet(network)
            ?? throw new RunTestPaymentExecutionException("wallet not available");
        var confirmedCoins = await wallet.GetUnspentCoins(payer.AccountDerivation, true, cancellationToken).ConfigureAwait(false);
        var confirmedTotal = confirmedCoins.Sum(coin => coin.Value.GetValue(network));
        var requiredAmount = paymentAmount + FundingBufferBtc;
        if (confirmedTotal >= requiredAmount)
        {
            return;
        }

        var explorerClient = _explorerClientProvider.GetExplorerClient(network);
        var rpc = explorerClient.RPCClient ?? throw new RunTestPaymentExecutionException("node RPC is not available");
        var fundingAddress = await explorerClient.GetUnusedAsync(payer.AccountDerivation, DerivationFeature.Deposit, 0, false, cancellationToken).ConfigureAwait(false);
        var amountToFund = Money.Coins(requiredAmount - confirmedTotal);
        var txId = await rpc.SendToAddressAsync(fundingAddress.Address, amountToFund, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (txId == uint256.Zero)
        {
            throw new RunTestPaymentExecutionException("failed to fund cheat-mode payer wallet");
        }

        var rewardAddress = await rpc.GetNewAddressAsync(cancellationToken).ConfigureAwait(false);
        await rpc.GenerateToAddressAsync(1, rewardAddress, cancellationToken).ConfigureAwait(false);

        for (var attempt = 0; attempt < GetAttemptCount(FundingConfirmationTimeout); attempt++)
        {
            var refreshedConfirmedCoins = await wallet.GetUnspentCoins(payer.AccountDerivation, true, cancellationToken).ConfigureAwait(false);
            var refreshedConfirmedTotal = refreshedConfirmedCoins.Sum(coin => coin.Value.GetValue(network));
            if (refreshedConfirmedTotal >= requiredAmount)
            {
                return;
            }

            await Task.Delay(FundingPollDelay, cancellationToken).ConfigureAwait(false);
        }

        throw new RunTestPaymentExecutionException("cheat-mode payer wallet funding was not confirmed in time");
    }

    private static int GetAttemptCount(TimeSpan timeout)
    {
        var attempts = (int)Math.Ceiling(timeout.TotalMilliseconds / FundingPollDelay.TotalMilliseconds);
        return Math.Max(attempts, 1);
    }

    private async Task<CreateSenderPsbtResult> CreateSenderPsbtAsync(RunTestPaymentContext runTestPaymentContext, CancellationToken cancellationToken)
    {
        var wallet = _walletProvider.GetWallet(runTestPaymentContext.Network);
        if (wallet is null)
        {
            throw new RunTestPaymentExecutionException("wallet not available");
        }

        var confirmedCoins = await wallet.GetUnspentCoins(runTestPaymentContext.PayerAccountDerivation, true, cancellationToken).ConfigureAwait(false);
        var allCoins = await wallet.GetUnspentCoins(runTestPaymentContext.PayerAccountDerivation, false, cancellationToken).ConfigureAwait(false);
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
            var createResult = await client.CreatePSBTAsync(runTestPaymentContext.PayerAccountDerivation, psbtRequest, cancellationToken).ConfigureAwait(false);
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
            var postResponse = await SendRequestAsync(postContext.Request, cancellationToken).ConfigureAwait(false);
            using var withReplyTransition = withReplyKey.ProcessResponse(postResponse, postContext.OhttpCtx);

            var current = withReplyTransition.Save(senderPersister);
            try
            {
                for (var attempt = 0; attempt < MaxProposalPollAttempts; attempt++)
                {
                    using var pollRequest = current.CreatePollRequest(ohttpRelayUrl.ToString());
                    var pollResponse = await SendRequestAsync(pollRequest.Request, cancellationToken).ConfigureAwait(false);
                    using var pollTransition = current.ProcessResponse(pollResponse, pollRequest.OhttpCtx);
                    var outcome = pollTransition.Save(senderPersister);

                    try
                    {
                        switch (outcome)
                        {
                            case PollingForProposalTransitionOutcome.Progress progress:
                                proposalPsbtBase64 = progress.PsbtBase64;
                                break;
                            case PollingForProposalTransitionOutcome.Stasis stasis:
                                current.Dispose();
                                current = stasis.Inner;
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

    private async Task<string> SignAndBroadcastAsync(RunTestPaymentContext runTestPaymentContext, PSBT proposalPsbt, CancellationToken cancellationToken)
    {
        var client = _explorerClientProvider.GetExplorerClient(runTestPaymentContext.Network);
        var payerAccountDerivation = runTestPaymentContext.PayerAccountDerivation
            ?? throw new RunTestPaymentExecutionException("payer derivation is not available");
        var accountKey = runTestPaymentContext.PayerAccountKey
            ?? throw new RunTestPaymentExecutionException("payer key is not available");
        var rootedKeyPath = new RootedKeyPath(accountKey.Neuter().PubKey.GetHDFingerPrint(), new KeyPath());
        proposalPsbt.RebaseKeyPaths(accountKey.Neuter(), rootedKeyPath);
        proposalPsbt.SignAll(payerAccountDerivation, accountKey, rootedKeyPath);

        if (!proposalPsbt.TryFinalize(out _))
        {
            throw new RunTestPaymentExecutionException("PSBT could not be finalized");
        }

        var transaction = proposalPsbt.ExtractTransaction();

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
        using var message = new HttpRequestMessage(HttpMethod.Post, request.Url)
        {
            Content = new ByteArrayContent(request.Body)
        };
        message.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(request.ContentType);

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
    private sealed record CheatModePayer(DerivationStrategyBase AccountDerivation, ExtKey AccountKey);
}

public sealed record RunTestPaymentContext(
    string InvoiceId,
    SystemUri PaymentUrl,
    SystemUri OhttpRelayUrl,
    BitcoinAddress PaymentAddress,
    decimal PaymentAmount,
    BTCPayNetwork Network,
    DerivationStrategyBase? PayerAccountDerivation = null,
    ExtKey? PayerAccountKey = null);
