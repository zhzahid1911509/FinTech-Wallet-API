using System.ComponentModel.DataAnnotations;
namespace FinTechWalletAPI.Contracts;
public sealed record EmailRequest([property: Required, EmailAddress, StringLength(254)] string Email);
public sealed record ConfirmEmailRequest(
    [property: Required, EmailAddress, StringLength(254)] string Email,
    [property: Required, StringLength(128, MinimumLength = 32)] string Token);
public sealed record ResetPasswordRequest(
    [property: Required, EmailAddress, StringLength(254)] string Email,
    [property: Required, StringLength(128, MinimumLength = 32)] string Token,
    [property: Required, StringLength(128, MinimumLength = 12)] string NewPassword);
public sealed record WalletStateRequest(bool IsFrozen,
    [property: Required, StringLength(500, MinimumLength = 5)] string Reason,
    [property: Required, StringLength(128)] string CurrentPassword);
public sealed record SessionResponse(Guid Id, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, bool IsCurrent);
