namespace FinTechWalletAPI.Models;
public sealed class TransferAuthorization
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public AuthSession Session { get; set; } = null!;
    public string RequestHash { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public Guid? ConsumedByTransferId { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
