using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
namespace FinTechWalletAPI.Services;
public static class ChallengeTokens
{
    public const string VerifyEmail = "VerifyEmail";
    public const string ResetPassword = "ResetPassword";
    public static string Create() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    public static string Hash(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    public static bool IsUsable(Models.AccountChallenge token, string purpose, string stamp, DateTimeOffset now) =>
        token.Purpose == purpose && token.UsedAt == null && token.ExpiresAt > now && token.SecurityStamp == stamp;
}
