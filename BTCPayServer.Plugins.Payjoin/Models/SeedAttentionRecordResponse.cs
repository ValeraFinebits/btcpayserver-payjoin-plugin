namespace BTCPayServer.Plugins.Payjoin.Models;

public sealed record SeedAttentionRecordResponse(bool Succeeded, string Message, string? Status = null)
{
    public static SeedAttentionRecordResponse Failure(string message)
    {
        return new(false, message);
    }

    public static SeedAttentionRecordResponse Success(string message, string status)
    {
        return new(true, message, status);
    }
}
