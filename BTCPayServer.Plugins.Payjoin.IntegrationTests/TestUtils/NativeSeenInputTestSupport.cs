using BTCPayServer.Plugins.Payjoin.Services;
using NBitcoin;
using Payjoin;
using OutPoint = NBitcoin.OutPoint;
using TxIn = NBitcoin.TxIn;
using Uri = System.Uri;

namespace BTCPayServer.Plugins.Payjoin.IntegrationTests.TestUtils;

internal sealed class NativeSeenInputTestSupport : IDisposable
{
    private readonly TestServices _services;
    private readonly PayjoinReceiverSessionStore _store;
    private readonly HttpClient _http;
    private readonly HttpClientHandler _httpHandler;
    private readonly Uri _relay;

    public NativeSeenInputTestSupport(TestServices services, PayjoinReceiverSessionStore store)
    {
        _services = services;
        _store = store;
        _relay = new Uri(services.OhttpRelayUrl());
        var cert = services.Cert();
        _httpHandler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (request, certificate, _, _) =>
                request.RequestUri!.Host == "localhost" && certificate is not null && certificate.RawData.AsSpan().SequenceEqual(cert)
        };
        _http = new HttpClient(_httpHandler, disposeHandler: false);
    }

    public async Task<MaybeInputsSeen> PrepareAsync(string invoiceId, Key senderKey, Coin[] coins, CancellationToken cancellationToken)
    {
        using var receiverKey = new Key();
        var receiverAddress = receiverKey.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        using var keys = _services.FetchOhttpKeys();
        using var receiverBuilder = new ReceiverBuilder(receiverAddress.ToString(), _services.DirectoryUrl(), keys);
        using var pinnedBuilder = receiverBuilder.WithAmount(50_000);
        using var bootstrap = pinnedBuilder.Build();
        var capture = new CapturingReceiverSessionPersister();
        using var receiver = bootstrap.Save(capture);
        var session = _store.GetOrCreateSession(invoiceId, receiverAddress.ToString(), "store-native-seen-inputs",
            DateTimeOffset.UtcNow.AddMinutes(20), capture.Events,
            new CreatePayjoinAccountingBridgeRequest(invoiceId, "store-native-seen-inputs", "BTC", "BTC-CHAIN",
                DateTimeOffset.UtcNow.AddMinutes(20), EffectiveInvoiceValueSats: 50_000));
        var persister = _store.CreatePersister(session);

        var senderAddress = senderKey.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        var transaction = Network.RegTest.CreateTransaction();
        foreach (var coin in coins)
        {
            transaction.Inputs.Add(new TxIn(coin.Outpoint));
        }
        transaction.Outputs.Add(Money.Satoshis(50_000), receiverAddress);
        transaction.Outputs.Add(Money.Satoshis(coins.Sum(coin => coin.Amount.Satoshi) - 51_000), senderAddress);
        var psbt = PSBT.FromTransaction(transaction, Network.RegTest);
        psbt.AddCoins(coins);
        psbt.SignWithKeys(senderKey).Finalize();

        using var uri = receiver.PjUri();
        using var senderBuilder = new SenderBuilder(psbt.ToBase64(), uri);
        using var senderBootstrap = senderBuilder.BuildRecommended(1000);
        var senderPersister = new SenderPersister();
        using var sender = senderBootstrap.Save(senderPersister);
        using var post = sender.CreateV2PostRequest(_relay.AbsoluteUri);
        var postBody = await SendAsync(post.Request, cancellationToken).ConfigureAwait(false);
        using var postResponse = sender.ProcessResponse(postBody, post.OhttpCtx);
        using var pollingSender = postResponse.Save(senderPersister);

        using var poll = receiver.CreatePollRequest(new UriBuilder(_relay) { Scheme = "https" }.Uri.AbsoluteUri);
        var pollBody = await SendAsync(poll.Request, cancellationToken).ConfigureAwait(false);
        using var response = receiver.ProcessResponse(pollBody, poll.ClientResponse);
        using var outcome = response.Save(persister);
        if (outcome is not InitializedTransitionOutcome.Progress progress)
        {
            throw new InvalidOperationException("The native receiver did not retrieve the original proposal.");
        }
        using var interactive = progress.Inner.AssumeInteractiveReceiver();
        using var maybeOwned = interactive.Save(persister);
        using var ownership = maybeOwned.CheckInputsNotOwned(new ForeignInput());
        var seen = ownership.Save(persister);
        _store.AttachOriginalPayment(persister,
            new PayjoinReceiverPaymentValidation.OriginalPayment(50_000, transaction.GetHash().ToString(), 0));
        return seen;
    }

    private async Task<byte[]> SendAsync(Request request, CancellationToken cancellationToken)
    {
        var url = new Uri(request.Url);
        if (url.Host == "localhost" && url.Port == _relay.Port)
        {
            url = new UriBuilder(url) { Scheme = "http" }.Uri;
        }
        using var content = new ByteArrayContent(request.Body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(request.ContentType);
        using var response = await _http.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _http.Dispose();
        _httpHandler.Dispose();
    }

    private sealed class ForeignInput : IsInputOwned
    {
        public bool Callback(global::Payjoin.OutPoint outpoint) => false;
    }

    private sealed class SenderPersister : JsonSenderSessionPersister
    {
        private readonly List<string> _events = [];
        public void Save(string @event) => _events.Add(@event);
        public string[] Load() => _events.ToArray();
        public void Close() { }
    }
}

internal sealed class RecordingSeenInputCallback(IsOutputKnown inner) : IsOutputKnown
{
    public List<(OutPoint Input, bool WasSeen)> Calls { get; } = [];

    public bool Callback(global::Payjoin.OutPoint outpoint)
    {
        var wasSeen = inner.Callback(outpoint);
        Calls.Add((new OutPoint(uint256.Parse(outpoint.Txid), outpoint.Vout), wasSeen));
        return wasSeen;
    }
}
