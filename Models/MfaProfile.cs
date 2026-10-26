namespace FinTechWalletAPI.Models;
public sealed class MfaProfile
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string ProtectedSecret { get; set; } = "";
    public DateTimeOffset? EnrollmentExpiresAt { get; set; }
    public long LastAcceptedStep { get; set; } = -1;
    public int FailedAttempts { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
public sealed class MfaRecoveryCode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string Hash { get; set; } = "";
    public DateTimeOffset? UsedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
public sealed class WalletApproval
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WalletId { get; set; }
    public Wallet Wallet { get; set; } = null!;
    public long WalletStateVersion { get; set; }
    public Guid RequestedBy { get; set; }
    public string RequesterSecurityStamp { get; set; } = "";
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string Reason { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public Guid? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? ReviewReason { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
