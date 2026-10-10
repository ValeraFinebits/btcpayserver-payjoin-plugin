using BTCPayServer.Plugins.Payjoin.Models;
using BTCPayServer.Plugins.Payjoin.Services;
using BTCPayServer.Services.Wallets;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NSubstitute;
using Payjoin;
using System.Net;
using Xunit;
using OutPoint = NBitcoin.OutPoint;
using TxOut = NBitcoin.TxOut;
using Uri = System.Uri;

namespace BTCPayServer.Plugins.Payjoin.Tests.Services;

public class PayjoinReceiverRelayFlowTests
{
    [Fact]
    public async Task SameInstanceRejectionPreservesSessionAndUriUntilAnotherRelayIsAvailable()
    {
        using var fixture = new ReceiverFixture();
        var rejectedRelay = new Uri(fixture.Services.DirectoryUrl());
        fixture.UseRelays(rejectedRelay);
        using var originalUri = fixture.Receiver.PjUri();

        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        Assert.True(fixture.Store.TryGetSession("invoice", out _));
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        var rejection = Assert.Single(fixture.Transport.Responses);
        Assert.Equal(HttpStatusCode.Forbidden, rejection.Status);
        Assert.Equal(0, rejection.Size);
        Assert.Equal(rejectedRelay, fixture.Manager.ChooseRelayForRequest([rejectedRelay], "another-invoice"));

        fixture.UseRelays(rejectedRelay, fixture.Transport.HealthyRelay);
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);

        using var replay = fixture.Replay();
        using var state = replay.State();
        var initialized = Assert.IsType<ReceiveSession.Initialized>(state);
        using var retainedUri = initialized.Inner.PjUri();
        Assert.Equal(originalUri.AsString(), retainedUri.AsString());
        Assert.Equal(2, fixture.Transport.Responses.Count);
        Assert.Equal((HttpStatusCode.OK, 8192), fixture.Transport.Responses[1]);
    }

    [Fact]
    public async Task TruncatedPollResponsePreservesSessionForNextTick()
    {
        using var fixture = new ReceiverFixture(TimeSpan.Zero);
        fixture.Transport.ResponseBody = [];

        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        Assert.True(fixture.Store.TryGetSession("invoice", out _));
        fixture.Transport.ResponseBody = null;
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);

        using var replay = fixture.Replay();
        using var state = replay.State();
        Assert.IsType<ReceiveSession.Initialized>(state);
        Assert.Equal(2, fixture.Transport.Responses.Count);
        Assert.Equal((HttpStatusCode.OK, 8192), fixture.Transport.Responses[1]);
    }

    [Fact]
    public async Task SuccessfulPollKeepsWantsOutputsAliveAcrossAsyncContinuation()
    {
        using var fixture = new ReceiverFixture();
        await fixture.PostOriginalAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var testCancellation = global::Xunit.TestContext.Current.CancellationToken;
        Task? continuation = null;

        var processing = fixture.StateProcessor.ProcessInitializedAsync(fixture.Context(), fixture.Receiver, (proposal, _, cancellationToken) =>
        {
            continuation = ContinueAsync(proposal, cancellationToken);
            entered.SetResult();
            return continuation;
        }, testCancellation);

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), testCancellation);
            Assert.False(processing.IsCompleted);
        }
        finally
        {
            resume.TrySetResult();
            await Task.WhenAll(processing, continuation ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5), testCancellation);
        }

        Assert.NotNull(continuation);
        using var replay = fixture.Replay();
        using var state = replay.State();
        Assert.IsType<ReceiveSession.WantsOutputs>(state);
        Assert.Equal(fixture.Transport.HealthyRelay, fixture.Manager.ChooseRelayForRequest([fixture.Transport.HealthyRelay], "invoice"));

        async Task ContinueAsync(WantsOutputs proposal, CancellationToken cancellationToken)
        {
            await resume.Task.WaitAsync(cancellationToken).ConfigureAwait(true);
            Assert.True(proposal.ProposalTxidIsStable());
        }
    }

    [Fact]
    public async Task WalletFailureAfterOriginalDoesNotQuarantineSuccessfulRelay()
    {
        using var fixture = new ReceiverFixture();
        await fixture.PostOriginalAsync();
        fixture.Wallet.FailInputLookup = true;

        await fixture.Processor.ProcessTickAsync(CancellationToken.None);

        Assert.Equal(1, fixture.Wallet.InputLookupCalls);
        using var replay = fixture.Replay();
        using var state = replay.State();
        Assert.IsType<ReceiveSession.MaybeInputsOwned>(state);
        Assert.Equal(fixture.Transport.HealthyRelay, fixture.Manager.ChooseRelayForRequest([fixture.Transport.HealthyRelay], "invoice"));
    }

    [Fact]
    public async Task PollPersistenceFailureDoesNotQuarantineRelay()
    {
        using var fixture = new ReceiverFixture();
        await fixture.PostOriginalAsync();
        fixture.Factory.FailSaveChanges = true;

        using var error = await Assert.ThrowsAsync<ReceiverPersistedException.Storage>(() =>
            fixture.StateProcessor.ProcessInitializedAsync(fixture.Context(), fixture.Receiver,
                static (_, _, _) => throw new InvalidOperationException("Persistence must precede wallet processing."), CancellationToken.None));

        fixture.Factory.FailSaveChanges = false;
        Assert.Equal(fixture.Transport.HealthyRelay, fixture.Manager.ChooseRelayForRequest([fixture.Transport.HealthyRelay], "invoice"));
        using var replay = fixture.Replay();
        using var state = replay.State();
        Assert.IsType<ReceiveSession.Initialized>(state);
    }

    [Fact]
    public async Task FatalPollResponseRemainsFatalWithoutQuarantiningRelay()
    {
        using var fixture = new ReceiverFixture();
        fixture.Transport.ResponseBody = new byte[8192];

        using var error = await Assert.ThrowsAsync<ReceiverPersistedException.Fatal>(() =>
            fixture.StateProcessor.ProcessInitializedAsync(fixture.Context(), fixture.Receiver,
                static (_, _, _) => throw new InvalidOperationException("Invalid ciphertext must not advance."), CancellationToken.None));

        Assert.Equal(fixture.Transport.HealthyRelay, fixture.Manager.ChooseRelayForRequest([fixture.Transport.HealthyRelay], "invoice"));
    }

    [Fact]
    public async Task TruncatedErrorReplyResponseQuarantinesRelayUntilRetryAfterExpiry()
    {
        var now = DateTimeOffset.UnixEpoch;
        var clock = Substitute.For<TimeProvider>();
        clock.GetUtcNow().Returns(_ => now);
        var quarantineDuration = TimeSpan.FromMinutes(10);
        using var fixture = new ReceiverFixture(quarantineDuration, clock);
        await fixture.PrepareReplyableErrorAsync();
        var eventsBeforeReply = fixture.Events();
        var responsesBeforeReply = fixture.Transport.Responses.Count;
        fixture.Transport.ResponseBody = [];

        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        Assert.Equal(responsesBeforeReply + 1, fixture.Transport.Responses.Count);
        Assert.Equal(eventsBeforeReply, fixture.Events());
        Assert.Null(fixture.Manager.ChooseRelayForRequest([fixture.Transport.HealthyRelay], "invoice"));
        Assert.Equal(fixture.Transport.HealthyRelay, fixture.Manager.ChooseRelayForRequest([fixture.Transport.HealthyRelay], "another-invoice"));
        using (var replay = fixture.Replay())
        using (var state = replay.State())
        {
            Assert.IsType<ReceiveSession.HasReplyableError>(state);
        }

        fixture.Transport.ResponseBody = null;
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        Assert.Equal(responsesBeforeReply + 1, fixture.Transport.Responses.Count);
        now += quarantineDuration - TimeSpan.FromTicks(1);
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        Assert.Equal(responsesBeforeReply + 1, fixture.Transport.Responses.Count);
        Assert.Equal(eventsBeforeReply, fixture.Events());

        now += TimeSpan.FromTicks(1);
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        Assert.Equal(responsesBeforeReply + 2, fixture.Transport.Responses.Count);
        using (var replay = fixture.Replay())
        using (var state = replay.State())
        {
            Assert.IsType<ReceiveSession.Closed>(state);
        }

        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        Assert.False(fixture.Store.TryGetSession("invoice", out _));
    }

    [Fact]
    public async Task RejectedPostRetriesPersistedProposalWithoutSigningAgain()
    {
        using var fixture = new ReceiverFixture();
        using var proposal = await fixture.PrepareProposalAsync();
        var expectedPsbt = proposal.Psbt();
        var eventsBeforePost = fixture.Events();
        fixture.UseRelays(new Uri(fixture.Services.DirectoryUrl()));

        await fixture.Processor.ProcessTickAsync(CancellationToken.None);
        Assert.True(fixture.Store.TryGetSession("invoice", out _));
        Assert.Equal(eventsBeforePost, fixture.Events());
        using (var replay = fixture.Replay())
        using (var state = replay.State())
        {
            Assert.Equal(expectedPsbt, Assert.IsType<ReceiveSession.PayjoinProposal>(state).Inner.Psbt());
        }

        fixture.UseRelays(new Uri(fixture.Services.DirectoryUrl()), fixture.Transport.HealthyRelay);
        await fixture.Processor.ProcessTickAsync(CancellationToken.None);

        using var completedReplay = fixture.Replay();
        using var completedState = completedReplay.State();
        Assert.IsType<ReceiveSession.Monitor>(completedState);
        Assert.Equal(0, fixture.ProposalSigner.Calls);
        Assert.Equal(eventsBeforePost.Length + 1, fixture.Events().Length);
    }

    [Fact]
    public async Task PostPersistenceFailureDoesNotQuarantineRelay()
    {
        using var fixture = new ReceiverFixture();
        using var proposal = await fixture.PrepareProposalAsync();
        fixture.Factory.FailSaveChanges = true;

        using var error = await Assert.ThrowsAsync<ReceiverPersistedException.Storage>(() =>
            fixture.Finalizer.PostAsync(new PayjoinReceiverProposalFinalizationContext(fixture.Context().Persister, "store", "invoice", PayjoinConstants.BitcoinCode),
                proposal, CancellationToken.None));

        fixture.Factory.FailSaveChanges = false;
        Assert.Equal(fixture.Transport.HealthyRelay, fixture.Manager.ChooseRelayForRequest([fixture.Transport.HealthyRelay], "invoice"));
    }

    private sealed class ReceiverFixture : IDisposable
    {
        private readonly Key _key = new();
        private readonly IPayjoinStoreSettingsRepository _settings = Substitute.For<IPayjoinStoreSettingsRepository>();
        private readonly HttpClient _http;
        public TestServices Services { get; } = TestServices.Initialize();
        public SqliteTestPayjoinPluginDbContextFactory Factory { get; } = new();
        public LocalMailroomHandler Transport { get; }
        public PayjoinMailroomManager Manager { get; }
        public PayjoinReceiverSessionStore Store { get; }
        public PayjoinReceiverStateProcessor StateProcessor { get; }
        public PayjoinReceiverSessionProcessor Processor { get; }
        public PayjoinReceiverProposalFinalizer Finalizer { get; }
        public Initialized Receiver { get; }
        public FaultingWallet Wallet { get; } = new();
        public RecordingProposalSigner ProposalSigner { get; }
        private PassthroughSigner Signer { get; } = new();
        private BitcoinAddress Address => _key.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);

        public ReceiverFixture(TimeSpan? quarantineDuration = null, TimeProvider? timeProvider = null)
        {
            Services.WaitForServicesReady();
            Transport = new LocalMailroomHandler(Services);
            _http = new HttpClient(Transport);
            Manager = new PayjoinMailroomManager(NullLogger<PayjoinMailroomManager>.Instance,
                quarantineDuration ?? TimeSpan.FromMinutes(10), (_, _, _, _) => throw new NotSupportedException(), timeProvider);
            Store = new PayjoinReceiverSessionStore(Factory, new SqliteUniqueConstraintViolationDetector(), Manager);
            using var keys = Services.FetchOhttpKeys();
            using var builder = new ReceiverBuilder(Address.ToString(), Services.DirectoryUrl(), keys);
            using var bootstrap = builder.Build();
            var capture = new CapturingReceiverSessionPersister();
            Receiver = bootstrap.Save(capture);
            Store.CreateSession("invoice", Address.ToString(), "store", DateTimeOffset.UtcNow.AddMinutes(20), capture.Events);
            UseRelays(Transport.HealthyRelay);
            var httpFactory = Substitute.For<IHttpClientFactory>();
            httpFactory.CreateClient(Arg.Any<string>()).Returns(_http);
            var sender = new PayjoinReceiverRelayRequestSender(_settings, Manager, new PayjoinReceiverRelayClient(httpFactory));
            StateProcessor = new PayjoinReceiverStateProcessor(Store, sender, Wallet,
                new PayjoinSeenInputStore(Factory, new SqliteUniqueConstraintViolationDetector()), Manager);
            var bridge = new PayjoinAccountingBridgeService(Factory, new SqliteUniqueConstraintViolationDetector(), new PayjoinSessionBuildLock());
            ProposalSigner = new RecordingProposalSigner(Signer);
            Finalizer = new PayjoinReceiverProposalFinalizer(sender, ProposalSigner, bridge, Store, null!);
            Processor = new PayjoinReceiverSessionProcessor(Store, new ReplayingGuard(Store, Address.ScriptPubKey.ToBytes()),
                StateProcessor, null!, null!, bridge, null!, null!, Finalizer, null!, NullLogger<PayjoinReceiverSessionProcessor>.Instance);
        }

        public void UseRelays(params Uri[] relays) => _settings.GetAsync("store").Returns(Task.FromResult(new PayjoinStoreSettings { OhttpRelayUrls = relays }));

        public PayjoinReceiverSessionState Session()
        {
            Assert.True(Store.TryGetSession("invoice", out var session));
            return Assert.IsType<PayjoinReceiverSessionState>(session);
        }

        public PayjoinReceiverStateContext Context() => new(Session(), Store.CreatePersister(Session()), Address.ScriptPubKey.ToBytes(), "store", "invoice", _ => false);
        public ReplayResult Replay() => PayjoinMethods.ReplayReceiverEventLog(Store.CreatePersister(Session()));

        public string[] Events()
        {
            using var context = Factory.CreateContext();
            return context.ReceiverSessionEvents.Where(e => e.InvoiceId == "invoice")
                .OrderBy(e => e.Sequence).Select(e => e.Event).ToArray();
        }

        public async Task PostOriginalAsync()
        {
            // Synthetic native SegWit coin: this exercises protocol state, not an on-chain payment.
            using var senderKey = new Key();
            var senderAddress = senderKey.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
            var coin = new Coin(new OutPoint(uint256.Parse(new string('a', 64)), 0), new TxOut(Money.Satoshis(100_000), senderAddress));
            var psbt = Network.RegTest.CreateTransactionBuilder().AddCoins(coin).AddKeys(senderKey)
                .Send(Address, Money.Satoshis(50_000)).SetChange(senderAddress).SendFees(Money.Satoshis(1_000)).BuildPSBT(true).Finalize();
            using var uri = Receiver.PjUri();
            using var builder = new SenderBuilder(psbt.ToBase64(), uri);
            using var transition = builder.BuildRecommended(1000);
            var persister = new SenderPersister();
            using var sender = transition.Save(persister);
            using var request = sender.CreateV2PostRequest(Services.OhttpRelayUrl());
            var body = await SendNativeRequestAsync(request.Request).ConfigureAwait(false);
            using var response = sender.ProcessResponse(body, request.OhttpCtx);
            using var polling = response.Save(persister);
        }

        private async Task<InitializedTransitionOutcome> ReceiveOriginalAsync()
        {
            await PostOriginalAsync().ConfigureAwait(false);
            using var request = Receiver.CreatePollRequest(Transport.HealthyRelay.AbsoluteUri);
            var body = await SendNativeRequestAsync(request.Request).ConfigureAwait(false);
            using var response = Receiver.ProcessResponse(body, request.ClientResponse);
            return response.Save(Context().Persister);
        }

        public async Task PrepareReplyableErrorAsync()
        {
            using var outcome = await ReceiveOriginalAsync().ConfigureAwait(false);
            var payload = Assert.IsType<InitializedTransitionOutcome.Progress>(outcome).Inner;
            using var rejection = payload.CheckBroadcastSuitability(null, new RejectBroadcast());
            using var error = Assert.Throws<ReceiverPersistedException.Storage>(() => rejection.Save(Context().Persister));
        }

        public async Task<PayjoinProposal> PrepareProposalAsync()
        {
            using var outcome = await ReceiveOriginalAsync().ConfigureAwait(false);
            var payload = Assert.IsType<InitializedTransitionOutcome.Progress>(outcome).Inner;
            var persister = Context().Persister;
            using var interactive = payload.AssumeInteractiveReceiver();
            using var owned = interactive.Save(persister);
            using var ownership = owned.CheckInputsNotOwned(new ForeignInput());
            using var seen = ownership.Save(persister);
            using var history = seen.CheckNoInputsSeenBefore(new UnseenInput());
            using var outputs = history.Save(persister);
            using var identify = outputs.IdentifyReceiverOutputs(new ReceiverOutput(Address.ScriptPubKey.ToBytes()));
            using var wantsOutputs = identify.Save(persister);
            using var commitOutputs = wantsOutputs.CommitOutputs();
            using var inputs = commitOutputs.Save(persister);
            using var commitInputs = inputs.CommitInputs();
            using var feeRange = commitInputs.Save(persister);
            using var fees = feeRange.ApplyFeeRange(null, null);
            using var provisional = fees.Save(persister);
            using var finalization = provisional.FinalizeProposal(Signer);
            return finalization.Save(persister);
        }

        private async Task<byte[]> SendNativeRequestAsync(Request request)
        {
            using var content = new ByteArrayContent(request.Body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(request.ContentType);
            using var response = await _http.PostAsync(new Uri(request.Url), content).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }

        public void Dispose()
        {
            Receiver.Dispose();
            _http.Dispose();
            Factory.Dispose();
            Services.Dispose();
            _key.Dispose();
        }
    }

    private sealed class ReplayingGuard(PayjoinReceiverSessionStore store, byte[] script) : IPayjoinReceiverSessionGuard
    {
        public Task<PayjoinReceiverSessionGuardResult?> TryPrepareAsync(PayjoinReceiverSessionState session, CancellationToken cancellationToken)
        {
            var persister = store.CreatePersister(session);
            var replay = PayjoinMethods.ReplayReceiverEventLog(persister);
            return Task.FromResult<PayjoinReceiverSessionGuardResult?>(new PayjoinReceiverSessionGuardResult(session, persister, script, replay, replay.State(), _ => false));
        }
    }

    private sealed class FaultingWallet : IPayjoinWalletOwnershipService
    {
        public bool FailInputLookup { get; set; }
        public int InputLookupCalls { get; private set; }
        public Task<PayjoinInputOwnershipResolver> CreateInputResolverAsync(string storeId, IReadOnlyCollection<OutPoint> candidateInputOutpoints, CancellationToken cancellationToken)
        {
            InputLookupCalls++;
            return FailInputLookup
                ? Task.FromException<PayjoinInputOwnershipResolver>(new HttpRequestException("injected wallet lookup failure"))
                : Task.FromResult(new PayjoinInputOwnershipResolver([]));
        }
        public Task<PayjoinScriptOwnershipResolver> CreateOutputResolverAsync(string storeId, byte[] receiverScript, IReadOnlyCollection<byte[]> candidateOutputScripts, CancellationToken cancellationToken)
            => Task.FromResult(new PayjoinScriptOwnershipResolver(receiverScript, []));
    }

    private sealed class RejectBroadcast : CanBroadcast
    {
        public bool Callback(byte[] tx) => false;
    }
    private sealed class ForeignInput : IsInputOwned
    {
        public bool Callback(global::Payjoin.OutPoint outpoint) => false;
    }
    private sealed class UnseenInput : IsOutputKnown
    {
        public bool Callback(global::Payjoin.OutPoint outpoint) => false;
    }
    private sealed class ReceiverOutput(byte[] receiverScript) : IsScriptOwned
    {
        public bool Callback(byte[] script) => script.AsSpan().SequenceEqual(receiverScript);
    }
    private sealed class RecordingProposalSigner(ProcessPsbt signer) : IPayjoinReceiverProposalSigner
    {
        public int Calls { get; private set; }

        public Task<ProcessPsbt> CreateContributedInputSignerAsync(string storeId, ReceivedCoin[] receiverCoins, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(signer);
        }
    }
    private sealed class PassthroughSigner : ProcessPsbt
    {
        public string Callback(string psbt) => psbt;
    }
    private sealed class SenderPersister : JsonSenderSessionPersister
    {
        private readonly List<string> _events = [];
        public void Save(string @event) => _events.Add(@event);
        public string[] Load() => _events.ToArray();
        public void Close() { }
    }

    private sealed class LocalMailroomHandler : DelegatingHandler
    {
        public Uri HealthyRelay { get; }
        public byte[]? ResponseBody { get; set; }
        public List<(HttpStatusCode Status, int Size)> Responses { get; } = [];

        public LocalMailroomHandler(TestServices services)
        {
            var cert = services.Cert();
            InnerHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (request, certificate, _, _) =>
                    request.RequestUri!.Host == "localhost" && certificate is not null && certificate.RawData.AsSpan().SequenceEqual(cert)
            };
            HealthyRelay = new UriBuilder(services.OhttpRelayUrl()) { Scheme = "https" }.Uri;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (ResponseBody is not null)
            {
                Responses.Add((HttpStatusCode.OK, ResponseBody.Length));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ResponseBody) };
            }

            if (request.RequestUri!.Host == "localhost" && request.RequestUri.Port == HealthyRelay.Port)
            {
                request.RequestUri = new UriBuilder(request.RequestUri) { Scheme = "http" }.Uri;
            }

            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            Responses.Add((response.StatusCode, body.Length));
            return response;
        }
    }
}
