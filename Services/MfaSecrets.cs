using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using OtpNet;
namespace FinTechWalletAPI.Services;
public sealed class MfaSecrets(IDataProtectionProvider protection)
{
    private readonly IDataProtector protector = protection.CreateProtector("FinTechWallet.MfaSecrets.v1");
    public string Create() => Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));
    public string Protect(string key) => protector.Protect(key);
    public string Unprotect(string value) => protector.Unprotect(value);
    public static bool Verify(string key, string? code, DateTimeOffset now, long lastStep, out long step)
    {
        step = -1;
        if (code is null || code.Length != 6 || code.Any(c => c < '0' || c > '9')) return false;
        return new Totp(Base32Encoding.ToBytes(key)).VerifyTotp(now.UtcDateTime, code, out step,
            new VerificationWindow(previous: 1, future: 1)) && step > lastStep;
    }
    public static string CreateRecoveryCode() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    public static string RecoveryHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim().ToUpperInvariant())));
    public static string AuthenticatorUri(string email, string key) =>
        $"otpauth://totp/{Uri.EscapeDataString("FinTechWallet:" + email)}?secret={Uri.EscapeDataString(key)}&issuer=FinTechWallet&algorithm=SHA1&digits=6&period=30";
}
