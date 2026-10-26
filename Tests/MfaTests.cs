using FinTechWalletAPI.Services;
using Microsoft.AspNetCore.DataProtection;
using OtpNet;
using Xunit;
namespace FinTechWalletAPI.Tests;
public sealed class MfaTests
{
    [Fact]
    public void RfcTotpVectorAndReplayProtection()
    {
        var key=Base32Encoding.ToString(System.Text.Encoding.ASCII.GetBytes("12345678901234567890"));
        var time=DateTimeOffset.FromUnixTimeSeconds(59);
        Assert.True(MfaSecrets.Verify(key,"287082",time,-1,out var step));
        Assert.Equal(1,step);
        Assert.False(MfaSecrets.Verify(key,"287082",time,step,out _));
        Assert.False(MfaSecrets.Verify(key,"287082",time.AddMinutes(3),-1,out _));
    }
    [Theory]
    [InlineData(null)][InlineData("")][InlineData("12345")][InlineData("1234567")][InlineData("ABCDEF")]
    public void MalformedOtpIsRejected(string? code) => Assert.False(MfaSecrets.Verify("JBSWY3DPEHPK3PXP",code,DateTimeOffset.UtcNow,-1,out _));
    [Fact]
    public void RecoveryCodesHaveEntropyAndStableCanonicalHashes()
    {
        var codes=Enumerable.Range(0,100).Select(_=>MfaSecrets.CreateRecoveryCode()).ToArray();
        Assert.Equal(100,codes.Distinct().Count());
        Assert.All(codes,code=>{Assert.Equal(32,code.Length);Assert.Equal(64,MfaSecrets.RecoveryHash(code).Length);Assert.Equal(MfaSecrets.RecoveryHash(code),MfaSecrets.RecoveryHash(" "+code.ToLowerInvariant()+" "));});
    }
    [Fact]
    public void SecretIsEncryptedAndIsolatedFromEmailPurpose()
    {
        var provider=new EphemeralDataProtectionProvider();var secrets=new MfaSecrets(provider);var key=secrets.Create();var encrypted=secrets.Protect(key);
        Assert.DoesNotContain(key,encrypted);Assert.Equal(key,secrets.Unprotect(encrypted));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(()=>new EmailEnvelope(provider).Unprotect(encrypted));
    }
    [Fact]
    public void AuthenticatorUriEncodesAccountLabel()
    {
        var uri=MfaSecrets.AuthenticatorUri("alice+test@example.com","JBSWY3DPEHPK3PXP");
        Assert.StartsWith("otpauth://totp/",uri);Assert.Contains("alice%2Btest%40example.com",uri);Assert.Contains("digits=6&period=30",uri);
    }
}
