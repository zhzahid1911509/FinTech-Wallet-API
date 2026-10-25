using FinTechWalletAPI.Infrastructure;
using FinTechWalletAPI.Models;
using FinTechWalletAPI.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MimeKit;
using Xunit;
namespace FinTechWalletAPI.Tests;
public sealed class AccountSecurityTests
{
    [Fact]
    public void ChallengeTokensAreRandomAndUrlSafe()
    {
        var values = Enumerable.Range(0, 100).Select(_ => ChallengeTokens.Create()).ToArray();
        Assert.Equal(100, values.Distinct().Count());
        Assert.All(values, value => {
            Assert.Equal(43, value.Length);
            Assert.All(value, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'));
            Assert.Equal(64, ChallengeTokens.Hash(value).Length);
            Assert.DoesNotContain(value, ChallengeTokens.Hash(value));
        });
    }
    [Fact]
    public void ExpiryPurposeConsumptionAndStampAreEnforced()
    {
        var now = DateTimeOffset.UtcNow;
        var token = new AccountChallenge { Purpose = ChallengeTokens.ResetPassword, SecurityStamp = "original", ExpiresAt = now.AddMinutes(1) };
        Assert.True(ChallengeTokens.IsUsable(token, ChallengeTokens.ResetPassword, "original", now));
        Assert.False(ChallengeTokens.IsUsable(token, ChallengeTokens.VerifyEmail, "original", now));
        Assert.False(ChallengeTokens.IsUsable(token, ChallengeTokens.ResetPassword, "changed", now));
        Assert.False(ChallengeTokens.IsUsable(token, ChallengeTokens.ResetPassword, "original", token.ExpiresAt));
        token.UsedAt = now;
        Assert.False(ChallengeTokens.IsUsable(token, ChallengeTokens.ResetPassword, "original", now));
    }
    [Fact]
    public void EmailPayloadIsProtectedAndTamperingFails()
    {
        var service = new EmailEnvelope(new EphemeralDataProtectionProvider());
        var payload = new EmailPayload("user@example.test", "Verify email", "Secret token example");
        var encrypted = service.Protect(payload);
        Assert.DoesNotContain(payload.To, encrypted); Assert.DoesNotContain(payload.Text, encrypted);
        Assert.Equal(payload, service.Unprotect(encrypted));
        var index = encrypted.Length / 2;
        var tampered = encrypted[..index] + (encrypted[index] == 'A' ? 'B' : 'A') + encrypted[(index+1)..];
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => service.Unprotect(tampered));
    }
    [Fact]
    public async Task PickupTransportWritesReadableEmailAndRetryUsesSameFilename()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wallet-pickup-" + Guid.NewGuid().ToString("N"));
        try {
            var settings = Options.Create(new EmailSettings { PickupDirectory = directory });
            var transport = new EmailTransport(settings, new TestEnvironment());
            var id = Guid.NewGuid(); var payload = new EmailPayload("user@example.test", "Verify email", "Token: abc123");
            await transport.SendAsync(id, payload, default); await transport.SendAsync(id, payload, default);
            var file = Assert.Single(Directory.GetFiles(directory));
            using var message = await MimeMessage.LoadAsync(file);
            Assert.Equal(payload.Subject, message.Subject); Assert.Contains("Token: abc123", message.TextBody);
        } finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task PickupTransportRejectsProduction()
    {
        var transport = new EmailTransport(Options.Create(new EmailSettings()), new TestEnvironment { EnvironmentName = "Production" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.SendAsync(Guid.NewGuid(), new EmailPayload("user@example.test", "Test", "Test"), default));
    }
    [Theory]
    [InlineData("invalid", null)][InlineData(null, "invalid")]
    public void InvalidHistoryFiltersFail(string? kind, string? direction) =>
        Assert.Throws<ApiException>(() => HistoryFilters.Validate(null, null, kind, direction));
    [Fact]
    public void HistoryDateRangeMustBeIncreasing()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Throws<ApiException>(() => HistoryFilters.Validate(now, now, null, null));
        HistoryFilters.Validate(now.AddDays(-1), now, "Transfer", "incoming");
    }
    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
