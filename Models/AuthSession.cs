namespace FinTechWalletAPI.Models;
public sealed class AuthSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
public sealed class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public AuthSession Session { get; set; } = null!;
    public string Hash { get; set; } = "";
    public DateTimeOffset? UsedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
