using System.ComponentModel.DataAnnotations;
namespace FinTechWalletAPI.Contracts;
public sealed record TransferAuthorizationRequest(Guid RecipientWalletId, decimal Amount,
    [property: Required, StringLength(128)] string CurrentPassword,
    [property: Required, RegularExpression("^[0-9]{6}$")] string AuthenticatorCode);
public sealed record TransferAuthorizationResponse(Guid Id, Guid RecipientWalletId, decimal Amount,
    string Currency, string IdempotencyKey, DateTimeOffset ExpiresAt);
public sealed record TransferRiskResponse(bool AuthorizationRequired, string[] Reasons, decimal OutgoingLast24Hours,
    bool MfaEnrolled, string PolicyVersion = "v1");
