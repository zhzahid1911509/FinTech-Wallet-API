using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FinTechWalletAPI.Contracts;
using FinTechWalletAPI.Data;
using FinTechWalletAPI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
namespace FinTechWalletAPI.Tests;
public sealed partial class SqlApiTests
{
    private async Task<TransferAuthorizationResponse> AuthorizeTransfer(TokenResponse session, string key, Guid recipient, decimal amount, string idempotency)
    {
        factory.Clock.Advance(TimeSpan.FromSeconds(31));
        var response = await AuthorizeTransferRaw(session, recipient, amount, idempotency, Code(key));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TransferAuthorizationResponse>())!;
    }
    private async Task<HttpResponseMessage> AuthorizeTransferRaw(TokenResponse session, Guid recipient, decimal amount, string idempotency, string code, string password = Password)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transfers/authorizations") {
            Content = JsonContent.Create(new TransferAuthorizationRequest(recipient, amount, password, code)) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        request.Headers.Add("Idempotency-Key", idempotency);
        return await client.SendAsync(request);
    }
    private async Task<TransferRiskResponse> Assess(TokenResponse session, Guid recipient, decimal amount)
    {
        var response = await Authorized(HttpMethod.Post, "/api/v1/transfers/assess", session.AccessToken, new TransferRequest(recipient, amount));
        response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<TransferRiskResponse>())!;
    }
    private void RollingThreshold(decimal amount) => factory.Services.GetRequiredService<IOptions<TransferRiskSettings>>().Value.Rolling24HoursThreshold = amount;
    [Fact]
    public async Task NewRecipientRiskRequiresEnrollmentAndDoesNotPost()
    {
        var alice = await Login("alice@example.test"); var bob = await Login("bob@example.test"); var target = await Wallet(bob.AccessToken);
        var risk = await Assess(alice, target.Id, 1000);
        Assert.True(risk.AuthorizationRequired); Assert.False(risk.MfaEnrolled); Assert.Contains("new_recipient", risk.Reasons);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(alice.AccessToken, target.Id, 1000, "risk-denied")).StatusCode);
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Transfers.AnyAsync(x => x.IdempotencyKey == "risk-denied"));
        Assert.True(await db.AuditEvents.AnyAsync(x => x.Action == "Transfer.RiskDenied"));
        Assert.Equal(10000m, (await Wallet(alice.AccessToken)).Balance);
    }
    [Fact]
    public async Task FreshLoginAndSessionStepUpDoNotReplaceTransactionAuthorization()
    {
        var enrollment = await Enroll("alice@example.test"); var alice = await LoginMfa("alice@example.test", enrollment.Key);
        var bob = await Login("bob@example.test"); var target = await Wallet(bob.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(alice.AccessToken, target.Id, 5000, "large")).StatusCode);
        var approval = await AuthorizeTransfer(alice, enrollment.Key, target.Id, 5000, "large");
        Assert.Equal(HttpStatusCode.Created, (await Send(alice.AccessToken, target.Id, 5000, "large", approval.Id)).StatusCode);
        factory.Clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(HttpStatusCode.OK, (await Send(alice.AccessToken, target.Id, 5000, "large")).StatusCode);
        Assert.Equal(5000m, (await Wallet(alice.AccessToken)).Balance);
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.NotNull((await db.TransferAuthorizations.SingleAsync()).ConsumedByTransferId);
        Assert.Equal(1, await db.AuditEvents.CountAsync(x => x.Action == "Transfer.AuthorizationConsumed"));
    }
    [Theory]
    [InlineData("amount")] [InlineData("key")] [InlineData("session")] [InlineData("actor")] [InlineData("recipient")]
    public async Task AuthorizationCannotBeUsedForDifferentDetails(string change)
    {
        var enrollment = await Enroll("alice@example.test"); var alice = await LoginMfa("alice@example.test", enrollment.Key);
        var bob = await Login("bob@example.test"); var target = await Wallet(bob.AccessToken);
        var approval = await AuthorizeTransfer(alice, enrollment.Key, target.Id, 1000, "bound");
        var token = alice.AccessToken; var recipient = target.Id;
        if (change == "session") { factory.Clock.Advance(TimeSpan.FromSeconds(31)); token = (await LoginMfa("alice@example.test", enrollment.Key)).AccessToken; }
        if (change == "actor") { token = bob.AccessToken; recipient = (await Wallet(alice.AccessToken)).Id; }
        if (change == "recipient") {
            (await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest("charlie@example.test", Password))).EnsureSuccessStatusCode();
            await ConfirmRegistration("charlie@example.test"); recipient = (await Wallet((await Login("charlie@example.test")).AccessToken)).Id;
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(token, recipient, change == "amount" ? 1001 : 1000, change == "key" ? "other" : "bound", approval.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send(alice.AccessToken, target.Id, 1000, "bound", approval.Id)).StatusCode);
    }
    [Fact]
    public async Task ExpiredApprovalDoesNotConsumeIdempotencyKey()
    {
        var enrollment = await Enroll("alice@example.test"); var alice = await LoginMfa("alice@example.test", enrollment.Key);
        var target = await Wallet((await Login("bob@example.test")).AccessToken);
        var approval = await AuthorizeTransfer(alice, enrollment.Key, target.Id, 1000, "expiry");
        factory.Clock.Advance(TimeSpan.FromSeconds(120));
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(alice.AccessToken, target.Id, 1000, "expiry", approval.Id)).StatusCode);
        var replacement = await AuthorizeTransfer(alice, enrollment.Key, target.Id, 1000, "expiry");
        Assert.Equal(HttpStatusCode.Created, (await Send(alice.AccessToken, target.Id, 1000, "expiry", replacement.Id)).StatusCode);
    }
    [Fact]
    public async Task RecoverySessionRequiresAuthorizationEvenForSmallTransfers()
    {
        var enrollment = await Enroll("alice@example.test");
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("alice@example.test", Password, RecoveryCode: enrollment.RecoveryCodes[0]));
        login.EnsureSuccessStatusCode(); var alice = (await login.Content.ReadFromJsonAsync<TokenResponse>())!;
        var target = await Wallet((await Login("bob@example.test")).AccessToken);
        Assert.Contains("recovery_session", (await Assess(alice, target.Id, 1)).Reasons);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(alice.AccessToken, target.Id, 1, "recovery-transfer")).StatusCode);
        var approval = await AuthorizeTransfer(alice, enrollment.Key, target.Id, 1, "recovery-transfer");
        Assert.Equal(HttpStatusCode.Created, (await Send(alice.AccessToken, target.Id, 1, "recovery-transfer", approval.Id)).StatusCode);
    }
    [Fact]
    public async Task ApprovalRequiresPasswordAndUnusedTotpAndPreservesFailureCounters()
    {
        var enrollment = await Enroll("alice@example.test"); var alice = await LoginMfa("alice@example.test", enrollment.Key);
        var target = await Wallet((await Login("bob@example.test")).AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await AuthorizeTransferRaw(alice, target.Id, 1000, "proof", Code(enrollment.Key), "wrong-password")).StatusCode);
        // Login consumed the current TOTP. Reuse must fail despite a correct password.
        Assert.Equal(HttpStatusCode.Forbidden, (await AuthorizeTransferRaw(alice, target.Id, 1000, "proof", Code(enrollment.Key))).StatusCode);
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, (await db.MfaProfiles.SingleAsync()).FailedAttempts);
        Assert.Empty(await db.TransferAuthorizations.ToListAsync());
    }
    [Fact]
    public async Task PostingFailureRollsBackAuthorizationConsumption()
    {
        var enrollment = await Enroll("alice@example.test"); var alice = await LoginMfa("alice@example.test", enrollment.Key);
        var target = await Wallet((await Login("bob@example.test")).AccessToken);
        var approval = await AuthorizeTransfer(alice, enrollment.Key, target.Id, 1234, "auth-rollback");
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE LedgerEntries ADD CONSTRAINT CK_TestApprovalFailure CHECK ([Debit] <> 1234 AND [Credit] <> 1234)");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Send(alice.AccessToken, target.Id, 1234, "auth-rollback", approval.Id)).StatusCode);
        Assert.Null((await db.TransferAuthorizations.AsNoTracking().SingleAsync()).ConsumedByTransferId);
        Assert.Equal(10000m, (await Wallet(alice.AccessToken)).Balance);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE LedgerEntries DROP CONSTRAINT CK_TestApprovalFailure");
        Assert.Equal(HttpStatusCode.Created, (await Send(alice.AccessToken, target.Id, 1234, "auth-rollback", approval.Id)).StatusCode);
    }
    [Fact]
    public async Task ConcurrentRequestsCannotBypassCumulativeThreshold()
    {
        RollingThreshold(1000);
        var alice = await Login("alice@example.test"); var target = await Wallet((await Login("bob@example.test")).AccessToken);
        Assert.Equal(HttpStatusCode.Created, (await Send(alice.AccessToken, target.Id, 1, "known")).StatusCode);
        var responses = await Task.WhenAll(Send(alice.AccessToken, target.Id, 600, "cumulative-1"), Send(alice.AccessToken, target.Id, 600, "cumulative-2"));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Created);
        Assert.All(responses, x => Assert.Contains(x.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.Forbidden, HttpStatusCode.Conflict }));
        Assert.Equal(9399m, (await Wallet(alice.AccessToken)).Balance);
        Assert.Contains("cumulative_spending", (await Assess(alice, target.Id, 400)).Reasons);
        factory.Clock.Advance(TimeSpan.FromHours(24).Add(TimeSpan.FromSeconds(1)));
        Assert.False((await Assess(alice, target.Id, 400)).AuthorizationRequired);
    }
    [Fact]
    public async Task ConcurrentApprovedDuplicatesConsumeOnce()
    {
        var enrollment = await Enroll("alice@example.test"); var alice = await LoginMfa("alice@example.test", enrollment.Key);
        var target = await Wallet((await Login("bob@example.test")).AccessToken);
        var approval = await AuthorizeTransfer(alice, enrollment.Key, target.Id, 1000, "approved-duplicate");
        var responses = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Send(alice.AccessToken, target.Id, 1000, "approved-duplicate", approval.Id)));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Created);
        Assert.All(responses, x => Assert.Contains(x.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK, HttpStatusCode.Conflict }));
        Assert.Equal(9000m, (await Wallet(alice.AccessToken)).Balance);
    }
    [Fact]
    public async Task RevokedSessionCannotExecuteItsApproval()
    {
        var enrollment = await Enroll("alice@example.test"); var alice = await LoginMfa("alice@example.test", enrollment.Key);
        var target = await Wallet((await Login("bob@example.test")).AccessToken);
        var approval = await AuthorizeTransfer(alice, enrollment.Key, target.Id, 1000, "revoked");
        (await Authorized(HttpMethod.Post, "/api/v1/auth/logout-all", alice.AccessToken, new {})).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(alice.AccessToken, target.Id, 1000, "revoked", approval.Id)).StatusCode);
    }
}
