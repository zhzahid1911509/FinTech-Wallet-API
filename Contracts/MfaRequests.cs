using System.ComponentModel.DataAnnotations;
namespace FinTechWalletAPI.Contracts;
public sealed record PasswordRequest([property: Required, StringLength(128)] string CurrentPassword);
public sealed record MfaConfirmRequest([property: Required, RegularExpression("^[0-9]{6}$")] string Code);
public sealed record MfaProofRequest(
    [property: Required, StringLength(128)] string CurrentPassword,
    [property: StringLength(6), RegularExpression("^[0-9]{6}$")] string? AuthenticatorCode = null,
    [property: StringLength(64)] string? RecoveryCode = null);
public sealed record ApprovalActionRequest(
    [property: Required, StringLength(128)] string CurrentPassword,
    [property: Required, StringLength(500, MinimumLength = 5)] string Reason);
public sealed record MfaEnrollmentResponse(string SharedKey, string AuthenticatorUri, DateTimeOffset ExpiresAt);
public sealed record MfaRecoveryResponse(string[] RecoveryCodes, string Message = "Save these codes securely. They will not be shown again. Sign in again.");
public sealed record WalletApprovalResponse(Guid Id, Guid WalletId, Guid RequestedBy, string Reason,
    string Status, DateTimeOffset RequestedAt, DateTimeOffset ExpiresAt, Guid? ReviewedBy, DateTimeOffset? ReviewedAt, string? ReviewReason);
