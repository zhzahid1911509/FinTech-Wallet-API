using System.ComponentModel.DataAnnotations;
namespace FinTechWalletAPI.Contracts;
public sealed record RegisterRequest(
    [property: Required, EmailAddress, StringLength(254)] string Email,
    [property: Required, StringLength(128, MinimumLength = 12)] string Password);
public sealed record LoginRequest(
    [property: Required, EmailAddress, StringLength(254)] string Email,
    [property: Required, StringLength(128)] string Password);
public sealed record RefreshRequest([property: Required, StringLength(128, MinimumLength = 32)] string RefreshToken);
public sealed record TransferRequest(Guid RecipientWalletId, decimal Amount);
public sealed record TokenResponse(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, Guid SessionId);
public sealed record WalletResponse(Guid Id, string Currency, decimal Balance, bool IsFrozen);
public sealed record TransferResponse(Guid Id, Guid SourceWalletId, Guid DestinationWalletId,
    decimal Amount, string Currency, string Kind, DateTimeOffset CreatedAt, string Status = "Posted");
public sealed record TransferOutcome(TransferResponse Transfer, bool Replayed);
