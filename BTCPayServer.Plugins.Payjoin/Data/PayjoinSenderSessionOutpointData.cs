namespace BTCPayServer.Plugins.Payjoin.Data;

/// <summary>
/// One coin a live sender session spends. The primary key is the outpoint itself, so the
/// database refuses a second live session for the same coin anywhere on the server — two stores
/// can share a wallet, so the guard is deliberately not store-scoped. Rows are inserted in the
/// same transaction that creates the session and deleted in the one that completes it, which
/// makes "a row exists" and "a live session holds the coin" the same statement.
/// </summary>
internal class PayjoinSenderSessionOutpointData
{
    // The outpoint as NBitcoin prints it: "txid-index".
    public string Outpoint { get; set; } = null!;

    public string SenderSessionId { get; set; } = null!;

    public PayjoinSenderSessionData Session { get; set; } = null!;
}
