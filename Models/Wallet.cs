namespace FinTechWalletAPI.Models;
public sealed class Wallet
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? UserId { get; set; }
    public User? User { get; set; }
    public string Currency { get; set; } = "BDT";
    public decimal Balance { get; set; }
    public bool IsSystem { get; set; }
    public bool IsFrozen { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public byte[] RowVersion { get; set; } = [];
}
