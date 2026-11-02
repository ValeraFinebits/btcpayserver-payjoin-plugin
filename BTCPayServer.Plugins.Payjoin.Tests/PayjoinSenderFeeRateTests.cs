using BTCPayServer.Plugins.Payjoin.Services;
using NBitcoin;
using Payjoin;
using Xunit;
using OutPoint = NBitcoin.OutPoint;
using TxOut = NBitcoin.TxOut;
using Uri = System.Uri;

namespace BTCPayServer.Plugins.Payjoin.Tests;

public class PayjoinSenderFeeRateTests
{
    // A P2WPKH input weighs 272 WU (68 vB) in the library's fee accounting.
    private const decimal ContributedInputVirtualSize = 68m;

    [Theory]
    [InlineData(25L, 25UL)]
    [InlineData(125L, 125UL)]
    [InlineData(250L, 250UL)]
    [InlineData(500L, 500UL)]
    [InlineData(0L, PayjoinSenderService.DefaultFeeRateSatPerKwu)]
    [InlineData(-1L, PayjoinSenderService.DefaultFeeRateSatPerKwu)]
    public void TheRecordedFeeRateIsGivenToTheLibraryUnchanged(long recorded, ulong expected) =>
        Assert.Equal(expected, PayjoinSenderService.ResolveMinFeeRate(recorded));

    // Hot-wallet funding and the off-server signature path both build the sender through
    // ResolveMinFeeRate, so this round covers the rate each of them gives the library.
    [Theory]
    [InlineData(0.1)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public async Task AReceiverInputIsAcceptedAtTheOperatorsFeeRate(double satPerVByte)
    {
        var feeRate = new FeeRate((decimal)satPerVByte);
        using var round = new NativeRound();
        var original = round.CreateOriginal(feeRate);

        var proposal = await round.RunAsync(original, PayjoinSenderService.ResolveMinFeeRate(PayjoinSenderService.ToSatPerKwu(feeRate)));

        var originalChange = original.ExtractTransaction().Outputs.Single(o => o.ScriptPubKey == round.SenderScript).Value;
        var proposalChange = proposal.Outputs.Single(o => o.ScriptPubKey == round.SenderScript).Value;
        Assert.Contains(proposal.Inputs, i => i.PrevOut == round.ReceiverCoin.Outpoint);
        Assert.InRange(originalChange - proposalChange, Money.Zero,
            Money.Satoshis((long)Math.Ceiling(feeRate.SatoshiPerByte * ContributedInputVirtualSize)));
    }

    private sealed class NativeRound : IDisposable
    {
        private readonly Key _senderKey = new();
        private readonly Key _receiverKey = new();
        private readonly HttpClient _http = new();
        private readonly TestServices _services = TestServices.Initialize();

        public NativeRound()
        {
            _services.WaitForServicesReady();
            // Synthetic coins: this exercises the library's fee checks, not an on-chain payment.
            SenderCoin = new Coin(new OutPoint(uint256.Parse(new string('a', 64)), 0), new TxOut(Money.Satoshis(100_000), SenderScript));
            ReceiverCoin = new Coin(new OutPoint(uint256.Parse(new string('b', 64)), 0), new TxOut(Money.Satoshis(100_000), ReceiverAddress));
        }

        public Script SenderScript => _senderKey.PubKey.GetScriptPubKey(ScriptPubKeyType.Segwit);
        private BitcoinAddress ReceiverAddress => _receiverKey.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        private Coin SenderCoin { get; }
        public Coin ReceiverCoin { get; }

        // Pays the rounded-up fee for the signed size, so the original itself relays.
        public PSBT CreateOriginal(FeeRate feeRate)
        {
            var size = Build(Money.Satoshis(1_000)).ExtractTransaction().GetVirtualSize();
            return Build(Money.Satoshis((long)Math.Ceiling(feeRate.SatoshiPerByte * size)));
        }

        private PSBT Build(Money fee) => Network.RegTest.CreateTransactionBuilder()
            .AddCoins(SenderCoin).AddKeys(_senderKey)
            .Send(ReceiverAddress, Money.Satoshis(50_000)).SetChange(SenderScript)
            .SendFees(fee).BuildPSBT(true).Finalize();

        public async Task<NBitcoin.Transaction> RunAsync(PSBT original, ulong minFeeRateSatPerKwu)
        {
            var relay = _services.OhttpRelayUrl();
            var receiverEvents = new MemoryPersister();
            var senderEvents = new MemoryPersister();
            using var keys = _services.FetchOhttpKeys();
            using var receiverBuilder = new ReceiverBuilder(ReceiverAddress.ToString(), _services.DirectoryUrl(), keys);
            using var receiverBootstrap = receiverBuilder.Build();
            using var receiver = receiverBootstrap.Save(receiverEvents);

            using var uri = receiver.PjUri();
            using var senderBuilder = new SenderBuilder(original.ToBase64(), uri);
            using var senderBootstrap = senderBuilder.BuildRecommended(minFeeRateSatPerKwu);
            using var sender = senderBootstrap.Save(senderEvents);
            using var post = sender.CreateV2PostRequest(relay);
            using var posted = sender.ProcessResponse(await SendAsync(post.Request).ConfigureAwait(false), post.OhttpCtx);
            using var polling = posted.Save(senderEvents);

            using (var poll = receiver.CreatePollRequest(relay))
            using (var received = receiver.ProcessResponse(await SendAsync(poll.Request).ConfigureAwait(false), poll.ClientResponse))
            using (var outcome = received.Save(receiverEvents))
            using (var proposal = BuildProposal(Assert.IsType<InitializedTransitionOutcome.Progress>(outcome).Inner, receiverEvents))
            using (var reply = proposal.CreatePostRequest(relay))
            using (var replied = proposal.ProcessResponse(await SendAsync(reply.Request).ConfigureAwait(false), reply.ClientResponse))
            using (replied.Save(receiverEvents))
            {
            }

            using var check = polling.CreatePollRequest(relay);
            using var checkedProposal = polling.ProcessResponse(await SendAsync(check.Request).ConfigureAwait(false), check.OhttpCtx);
            using var result = checkedProposal.Save(senderEvents);
            var psbt = Assert.IsType<PollingForProposalTransitionOutcome.Progress>(result).PsbtBase64;
            return PSBT.Parse(psbt, Network.RegTest).GetGlobalTransaction();
        }

        private PayjoinProposal BuildProposal(UncheckedOriginalPayload payload, MemoryPersister events)
        {
            var receiverScript = ReceiverAddress.ScriptPubKey.ToBytes();
            using var interactive = payload.AssumeInteractiveReceiver();
            using var owned = interactive.Save(events);
            using var ownership = owned.CheckInputsNotOwned(new NotOwned());
            using var seen = ownership.Save(events);
            using var history = seen.CheckNoInputsSeenBefore(new NotSeen());
            using var outputs = history.Save(events);
            using var identify = outputs.IdentifyReceiverOutputs(new OwnedScript(receiverScript));
            using var wantsOutputs = identify.Save(events);
            using var commitOutputs = wantsOutputs.CommitOutputs();
            using var wantsInputs = commitOutputs.Save(events);
            using var pair = InputPair.NewP2wpkh(
                new global::Payjoin.TxOut((ulong)ReceiverCoin.Amount.Satoshi, receiverScript),
                new global::Payjoin.OutPoint(ReceiverCoin.Outpoint.Hash.ToString(), ReceiverCoin.Outpoint.N));
            using var contributed = wantsInputs.ContributeInputs([pair]);
            using var commitInputs = contributed.CommitInputs();
            using var wantsFeeRange = commitInputs.Save(events);
            // The plugin's receiver applies the library's default fee range.
            using var fees = wantsFeeRange.ApplyFeeRange(null, null);
            using var provisional = fees.Save(events);
            using var finalization = provisional.FinalizeProposal(new ReceiverInputSigner(ReceiverCoin, _receiverKey));
            return finalization.Save(events);
        }

        private async Task<byte[]> SendAsync(Request request)
        {
            using var content = new ByteArrayContent(request.Body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(request.ContentType);
            using var response = await _http.PostAsync(new Uri(request.Url), content).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }

        public void Dispose()
        {
            _http.Dispose();
            _services.Dispose();
            _senderKey.Dispose();
            _receiverKey.Dispose();
        }
    }

    private sealed class ReceiverInputSigner(Coin coin, Key key) : ProcessPsbt
    {
        public string Callback(string psbt)
        {
            var proposal = PSBT.Parse(psbt, Network.RegTest);
            var input = proposal.Inputs.FindIndexedInput(coin.Outpoint)!;
            input.UpdateFromCoin(coin);
            input.Sign(key);
            Assert.True(input.TryFinalizeInput(out _));
            return proposal.ToBase64();
        }
    }

    private sealed class MemoryPersister : JsonReceiverSessionPersister, JsonSenderSessionPersister
    {
        private readonly List<string> _events = [];
        public void Save(string @event) => _events.Add(@event);
        public string[] Load() => _events.ToArray();
        public void Close() { }
    }

    private sealed class NotOwned : IsInputOwned
    {
        public bool Callback(global::Payjoin.OutPoint outpoint) => false;
    }

    private sealed class NotSeen : IsOutputKnown
    {
        public bool Callback(global::Payjoin.OutPoint outpoint) => false;
    }

    private sealed class OwnedScript(byte[] script) : IsScriptOwned
    {
        public bool Callback(byte[] candidate) => candidate.AsSpan().SequenceEqual(script);
    }
}
