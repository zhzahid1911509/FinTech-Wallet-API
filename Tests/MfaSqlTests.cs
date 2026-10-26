using System.Net;
using System.Net.Http.Json;
using FinTechWalletAPI.Contracts;
using FinTechWalletAPI.Data;
using FinTechWalletAPI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;
using Xunit;
namespace FinTechWalletAPI.Tests;
public sealed partial class SqlApiTests
{
    private const string Password = "Test-Only-Password-239!";
    private sealed record Enrollment(string Key, string[] RecoveryCodes, TokenResponse OriginalSession);
    private string Code(string key, int offsetSeconds = 0) => new Totp(Base32Encoding.ToBytes(key)).ComputeTotp(factory.Clock.GetUtcNow().AddSeconds(offsetSeconds).UtcDateTime);
    private async Task<Enrollment> Enroll(string email)
    {
        var login = await Login(email);
        var start = await Authorized(HttpMethod.Post, "/api/v1/auth/mfa/enrollment", login.AccessToken, new PasswordRequest(Password));
        start.EnsureSuccessStatusCode();
        var enrollment = (await start.Content.ReadFromJsonAsync<MfaEnrollmentResponse>())!;
        var confirm = await Authorized(HttpMethod.Post, "/api/v1/auth/mfa/confirm", login.AccessToken, new MfaConfirmRequest(Code(enrollment.SharedKey, -30)));
        confirm.EnsureSuccessStatusCode();
        var response = (await confirm.Content.ReadFromJsonAsync<MfaRecoveryResponse>())!;
        return new(enrollment.SharedKey, response.RecoveryCodes, login);
    }
    private async Task<TokenResponse> LoginMfa(string email, string key, string password = Password)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password, Code(key)));
        response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }
    private async Task GrantOperator(string email)
    {
        using var scope = factory.Services.CreateScope();
        await OperationsService.SetOperatorAsync(scope.ServiceProvider, email, true);
    }
    private async Task<WalletApprovalResponse> RequestUnfreeze(TokenResponse requester, Guid wallet)
    {
        var response = await Authorized(HttpMethod.Post, $"/api/v1/operations/wallets/{wallet}/unfreeze-requests", requester.AccessToken,
            new ApprovalActionRequest(Password, "Customer review completed"));
        response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<WalletApprovalResponse>())!;
    }
    private Task<HttpResponseMessage> Decide(TokenResponse reviewer, Guid id, string decision) => Authorized(HttpMethod.Post,
        $"/api/v1/operations/unfreeze-requests/{id}/{decision}", reviewer.AccessToken, new ApprovalActionRequest(Password, "Independent review completed"));
    private async Task<(TokenResponse Alice, TokenResponse Bob, Guid Wallet, Enrollment AliceMfa, Enrollment BobMfa)> Operators()
    {
        var a = await Enroll("alice@example.test"); var b = await Enroll("bob@example.test");
        var alice = await LoginMfa("alice@example.test", a.Key); var bob = await LoginMfa("bob@example.test", b.Key);
        await GrantOperator("alice@example.test"); await GrantOperator("bob@example.test");
        var wallet = await Wallet(bob.AccessToken);
        (await Authorized(HttpMethod.Put, $"/api/v1/operations/wallets/{wallet.Id}/state", alice.AccessToken,
            new WalletStateRequest(true, "Security review required", Password))).EnsureSuccessStatusCode();
        return (alice, bob, wallet.Id, a, b);
    }
    [Fact]
    public async Task MfaEnrollmentRevokesSessionsAndLoginRequiresSingleUseProof()
    {
        var setup = await Enroll("alice@example.test");
        Assert.Equal(10, setup.RecoveryCodes.Length);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/api/v1/wallets/me", setup.OriginalSession.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("alice@example.test", Password))).StatusCode);
        var login = await LoginMfa("alice@example.test", setup.Key);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("alice@example.test", Password, Code(setup.Key)))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Authorized(HttpMethod.Post, "/api/v1/auth/mfa/enrollment", login.AccessToken, new PasswordRequest(Password))).StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var profile = await db.MfaProfiles.SingleAsync();
        Assert.DoesNotContain(setup.Key, profile.ProtectedSecret);
        var stored = await db.MfaRecoveryCodes.Select(x => x.Hash).ToListAsync();
        Assert.All(setup.RecoveryCodes, code => Assert.DoesNotContain(code, stored));
    }
    [Fact]
    public async Task RecoveryLoginCannotPerformSensitiveOperationWithoutAuthenticatorStepUp()
    {
        var setup = await Enroll("alice@example.test");
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("alice@example.test", Password, RecoveryCode: setup.RecoveryCodes[0]));
        response.EnsureSuccessStatusCode(); var login = (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("alice@example.test", Password, RecoveryCode: setup.RecoveryCodes[0]))).StatusCode);
        await GrantOperator("alice@example.test");
        var wallet = await Wallet(login.AccessToken); var path = $"/api/v1/operations/wallets/{wallet.Id}/state";
        Assert.Equal(HttpStatusCode.Forbidden, (await Authorized(HttpMethod.Put, path, login.AccessToken, new WalletStateRequest(true, "Review required", Password))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Authorized(HttpMethod.Post, "/api/v1/auth/mfa/step-up", login.AccessToken,
            new MfaProofRequest(Password, RecoveryCode: setup.RecoveryCodes[1]))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Authorized(HttpMethod.Post, "/api/v1/auth/mfa/step-up", login.AccessToken, new MfaProofRequest(Password, Code(setup.Key)))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Authorized(HttpMethod.Put, path, login.AccessToken, new WalletStateRequest(true, "Review required", Password))).StatusCode);
    }
    [Fact]
    public async Task MfaFailuresLockIndependentlyOfCorrectPassword()
    {
        var setup = await Enroll("alice@example.test");
        var valid = new[] { Code(setup.Key,-30), Code(setup.Key), Code(setup.Key,30) };
        var invalid = Enumerable.Range(0, 10).Select(x => x.ToString("D6")).First(x => !valid.Contains(x));
        for (var i=0;i<5;i++) Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("alice@example.test", Password, invalid))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("alice@example.test", Password, Code(setup.Key)))).StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<AppDbContext>().MfaProfiles.SingleAsync()).LockedUntil > factory.Clock.GetUtcNow());
        factory.Clock.Advance(TimeSpan.FromMinutes(16));
        Assert.NotNull(await LoginMfa("alice@example.test", setup.Key));
    }
    [Fact]
    public async Task RecoveryProofCanDisableMfaButRequiresPasswordAndRevokesSession()
    {
        var setup = await Enroll("alice@example.test"); var login = await LoginMfa("alice@example.test", setup.Key);
        Assert.Equal(HttpStatusCode.Forbidden, (await Authorized(HttpMethod.Post, "/api/v1/auth/mfa/disable", login.AccessToken,
            new MfaProofRequest("wrong", RecoveryCode: setup.RecoveryCodes[0]))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Authorized(HttpMethod.Post, "/api/v1/auth/mfa/disable", login.AccessToken,
            new MfaProofRequest(Password, RecoveryCode: setup.RecoveryCodes[0]))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/api/v1/wallets/me", login.AccessToken)).StatusCode);
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.MfaProfiles.ToListAsync()); Assert.Empty(await db.MfaRecoveryCodes.ToListAsync());
        Assert.NotNull(await Login("alice@example.test"));
    }
    [Fact]
    public async Task RecoveryRegenerationInvalidatesOldCodesAndSessions()
    {
        var setup = await Enroll("alice@example.test"); var login = await LoginMfa("alice@example.test", setup.Key);
        factory.Clock.Advance(TimeSpan.FromSeconds(31));
        var response = await Authorized(HttpMethod.Post, "/api/v1/auth/mfa/recovery-codes", login.AccessToken, new MfaProofRequest(Password, Code(setup.Key)));
        response.EnsureSuccessStatusCode(); var regenerated = (await response.Content.ReadFromJsonAsync<MfaRecoveryResponse>())!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/api/v1/wallets/me", login.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("alice@example.test", Password, RecoveryCode: setup.RecoveryCodes[0]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("alice@example.test", Password, RecoveryCode: regenerated.RecoveryCodes[0]))).StatusCode);
    }
    [Fact]
    public async Task RefreshDoesNotRenewMfaFreshness()
    {
        var setup = await Enroll("alice@example.test"); var login = await LoginMfa("alice@example.test", setup.Key);
        await GrantOperator("alice@example.test"); var wallet = await Wallet(login.AccessToken);
        factory.Clock.Advance(TimeSpan.FromMinutes(6));
        var refresh = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(login.RefreshToken));
        refresh.EnsureSuccessStatusCode(); var rotated = (await refresh.Content.ReadFromJsonAsync<TokenResponse>())!;
        var path = $"/api/v1/operations/wallets/{wallet.Id}/state";
        Assert.Equal(HttpStatusCode.Forbidden, (await Authorized(HttpMethod.Put, path, rotated.AccessToken, new WalletStateRequest(true,"Review required",Password))).StatusCode);
        (await Authorized(HttpMethod.Post, "/api/v1/auth/mfa/step-up", rotated.AccessToken, new MfaProofRequest(Password, Code(setup.Key)))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await Authorized(HttpMethod.Put, path, rotated.AccessToken, new WalletStateRequest(true,"Review required",Password))).StatusCode);
    }
    [Fact]
    public async Task PasswordResetDoesNotDisableAuthenticator()
    {
        var setup = await Enroll("alice@example.test");
        await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new EmailRequest("alice@example.test"));
        var code = await LatestCode("alice@example.test", "Reset your wallet password");
        (await client.PostAsJsonAsync("/api/v1/auth/reset-password", new ResetPasswordRequest("alice@example.test",code,"Changed-Password-239!"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("alice@example.test","Changed-Password-239!"))).StatusCode);
        Assert.NotNull(await LoginMfa("alice@example.test", setup.Key, "Changed-Password-239!"));
    }
    [Fact]
    public async Task UnfreezeNeedsDifferentOperatorAndCannotReplay()
    {
        var ops = await Operators(); var request = await RequestUnfreeze(ops.Alice, ops.Wallet);
        Assert.Equal(HttpStatusCode.Forbidden, (await Decide(ops.Alice,request.Id,"approve")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Decide(ops.Bob,request.Id,"approve")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Decide(ops.Bob,request.Id,"approve")).StatusCode);
        var wallet = await Wallet(ops.Bob.AccessToken); Assert.False(wallet.IsFrozen); Assert.Equal(10000m,wallet.Balance);
    }
    [Fact]
    public async Task ApprovalExpiresAndDoesNotUnfreeze()
    {
        var ops = await Operators(); var request = await RequestUnfreeze(ops.Alice, ops.Wallet);
        factory.Clock.Advance(TimeSpan.FromMinutes(31));
        (await Authorized(HttpMethod.Post,"/api/v1/auth/mfa/step-up",ops.Bob.AccessToken,new MfaProofRequest(Password,Code(ops.BobMfa.Key)))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict,(await Decide(ops.Bob,request.Id,"approve")).StatusCode);
        Assert.True((await Wallet(ops.Bob.AccessToken)).IsFrozen);
        using var scope=factory.Services.CreateScope();
        Assert.Equal("Expired",(await scope.ServiceProvider.GetRequiredService<AppDbContext>().WalletApprovals.SingleAsync()).Status);
    }
    [Theory]
    [InlineData("role")][InlineData("stamp")][InlineData("state")]
    public async Task ChangedRequesterOrWalletInvalidatesApproval(string change)
    {
        var ops=await Operators(); var request=await RequestUnfreeze(ops.Alice,ops.Wallet);
        using var scope=factory.Services.CreateScope(); var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if(change=="role") await OperationsService.SetOperatorAsync(scope.ServiceProvider,"alice@example.test",false);
        if(change=="stamp") (await Authorized(HttpMethod.Post,"/api/v1/auth/logout-all",ops.Alice.AccessToken)).EnsureSuccessStatusCode();
        if(change=="state") await db.Wallets.Where(x=>x.Id==ops.Wallet).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.StateVersion,x=>x.StateVersion+1));
        Assert.Equal(HttpStatusCode.Conflict,(await Decide(ops.Bob,request.Id,"approve")).StatusCode);
        Assert.True((await Wallet(ops.Bob.AccessToken)).IsFrozen);
        Assert.Equal("Invalidated",(await db.WalletApprovals.AsNoTracking().SingleAsync()).Status);
    }
    [Fact]
    public async Task RejectAndCancelKeepWalletFrozen()
    {
        var ops=await Operators(); var first=await RequestUnfreeze(ops.Alice,ops.Wallet);
        Assert.Equal(HttpStatusCode.OK,(await Decide(ops.Bob,first.Id,"reject")).StatusCode);
        var second=await RequestUnfreeze(ops.Alice,ops.Wallet);
        Assert.Equal(HttpStatusCode.Forbidden,(await Decide(ops.Bob,second.Id,"cancel")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await Decide(ops.Alice,second.Id,"cancel")).StatusCode);
        Assert.True((await Wallet(ops.Bob.AccessToken)).IsFrozen);
    }
    [Fact]
    public async Task ApprovalAndAuditRollBackTogetherOnDatabaseFailure()
    {
        var ops=await Operators(); var request=await RequestUnfreeze(ops.Alice,ops.Wallet);
        using var scope=factory.Services.CreateScope(); var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE AuditEvents ADD CONSTRAINT CK_TestApprovalFailure CHECK ([Action] <> 'Wallet.Unfrozen')");
        Assert.Equal(HttpStatusCode.ServiceUnavailable,(await Decide(ops.Bob,request.Id,"approve")).StatusCode);
        Assert.True((await Wallet(ops.Bob.AccessToken)).IsFrozen);
        Assert.Equal("Pending",(await db.WalletApprovals.AsNoTracking().SingleAsync()).Status);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE AuditEvents DROP CONSTRAINT CK_TestApprovalFailure");
        Assert.Equal(HttpStatusCode.OK,(await Decide(ops.Bob,request.Id,"approve")).StatusCode);
    }
    [Fact]
    public async Task ConcurrentUseOfOneTotpCreatesAtMostOneSession()
    {
        var setup=await Enroll("alice@example.test"); var request=new LoginRequest("alice@example.test",Password,Code(setup.Key));
        var responses=await Task.WhenAll(client.PostAsJsonAsync("/api/v1/auth/login",request),client.PostAsJsonAsync("/api/v1/auth/login",request));
        Assert.Single(responses,x=>x.StatusCode==HttpStatusCode.OK);
        Assert.All(responses,x=>Assert.Contains(x.StatusCode,new[]{HttpStatusCode.OK,HttpStatusCode.Forbidden,HttpStatusCode.Conflict,HttpStatusCode.ServiceUnavailable}));
        using var scope=factory.Services.CreateScope();
        Assert.Equal(1,await scope.ServiceProvider.GetRequiredService<AppDbContext>().Sessions.CountAsync(x=>x.RevokedAt==null));
    }
}
