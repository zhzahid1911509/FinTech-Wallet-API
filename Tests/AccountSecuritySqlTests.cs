using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FinTechWalletAPI.Contracts;
using FinTechWalletAPI.Data;
using FinTechWalletAPI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
namespace FinTechWalletAPI.Tests;
public sealed partial class SqlApiTests
{
    private async Task<string> LatestCode(string email, string subject)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var envelope = scope.ServiceProvider.GetRequiredService<EmailEnvelope>();
        foreach (var item in await db.EmailOutbox.AsNoTracking().OrderByDescending(x => x.CreatedAt).ToListAsync())
        {
            if (item.ProtectedPayload.Length == 0) continue;
            var payload = envelope.Unprotect(item.ProtectedPayload);
            if (payload.To == email && payload.Subject == subject)
                return payload.Text.Split('\n').Single(x => x.StartsWith("Token: ", StringComparison.Ordinal))[7..].Trim();
        }
        throw new InvalidOperationException("Expected queued email not found.");
    }
    private async Task ConfirmRegistration(string email)
    {
        var code = await LatestCode(email, "Verify your wallet email");
        var response = await client.PostAsJsonAsync("/api/v1/auth/confirm-email", new ConfirmEmailRequest(email, code));
        response.EnsureSuccessStatusCode();
    }
    private async Task<HttpResponseMessage> Authorized(HttpMethod method, string path, string token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }
    [Fact]
    public async Task EmailMustBeVerifiedAndCodeIsSingleUse()
    {
        var email = "verify@example.test";
        (await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, "Test-Only-Password-239!"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, "Test-Only-Password-239!"))).StatusCode);
        var code = await LatestCode(email, "Verify your wallet email");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/auth/confirm-email", new ConfirmEmailRequest("bob@example.test", code))).StatusCode);
        await ConfirmRegistration(email);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/auth/confirm-email", new ConfirmEmailRequest(email, code))).StatusCode);
        Assert.NotNull(await Login(email));
    }
    [Fact]
    public async Task ResetPasswordInvalidatesAllSessionsAndCannotReplay()
    {
        var first = await Login("alice@example.test"); var second = await Login("alice@example.test");
        var requestResponse = await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new EmailRequest("alice@example.test"));
        Assert.Equal(HttpStatusCode.Accepted, requestResponse.StatusCode);
        var code = await LatestCode("alice@example.test", "Reset your wallet password");
        var reset = new ResetPasswordRequest("alice@example.test", code, "Changed-Password-239!");
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/auth/reset-password", reset)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/auth/reset-password", reset)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/api/v1/wallets/me", first.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/api/v1/wallets/me", second.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(second.RefreshToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("alice@example.test", "Test-Only-Password-239!"))).StatusCode);
        Assert.NotNull(await Login("alice@example.test", "Changed-Password-239!"));
    }
    [Fact]
    public async Task ExpiredTokenAndWrongPurposeCannotResetPassword()
    {
        await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new EmailRequest("alice@example.test"));
        var code = await LatestCode("alice@example.test", "Reset your wallet password");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/auth/confirm-email", new ConfirmEmailRequest("alice@example.test", code))).StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.AccountChallenges.ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/auth/reset-password", new ResetPasswordRequest("alice@example.test", code, "Changed-Password-239!"))).StatusCode);
        Assert.NotNull(await Login("alice@example.test"));
    }
    [Fact]
    public async Task RecoveryResponsesDoNotRevealEligibilityAndRequestsAreThrottled()
    {
        var known = await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new EmailRequest("alice@example.test"));
        var unknown = await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new EmailRequest("missing@example.test"));
        Assert.Equal(known.StatusCode, unknown.StatusCode);
        Assert.Equal(await known.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new EmailRequest("alice@example.test"));
        using var scope = factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().EmailOutbox.CountAsync());
    }
    [Fact]
    public async Task SessionRevocationEnforcesOwnershipAndRevokeAll()
    {
        var alice = await Login("alice@example.test"); var otherAlice = await Login("alice@example.test"); var bob = await Login("bob@example.test");
        Assert.Equal(HttpStatusCode.NotFound, (await Authorized(HttpMethod.Delete, $"/api/v1/auth/sessions/{bob.SessionId}", alice.AccessToken)).StatusCode);
        var sessions = (await (await Get("/api/v1/auth/sessions", alice.AccessToken)).Content.ReadFromJsonAsync<List<SessionResponse>>())!;
        Assert.Equal(2, sessions.Count); Assert.Single(sessions, s => s.IsCurrent);
        Assert.Equal(HttpStatusCode.NoContent, (await Authorized(HttpMethod.Delete, $"/api/v1/auth/sessions/{otherAlice.SessionId}", alice.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/api/v1/wallets/me", otherAlice.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Authorized(HttpMethod.Post, "/api/v1/auth/logout-all", alice.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/api/v1/wallets/me", alice.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get("/api/v1/wallets/me", bob.AccessToken)).StatusCode);
    }
    [Fact]
    public async Task OperatorFreezeNeedsRoleAndPasswordAndIsAudited()
    {
        var alice = await Login("alice@example.test"); var bob = await Login("bob@example.test");
        var target = await Wallet(bob.AccessToken);
        var path = $"/api/v1/operations/wallets/{target.Id}/state";
        var request = new WalletStateRequest(true, "Manual security review", "Test-Only-Password-239!");
        Assert.Equal(HttpStatusCode.Forbidden, (await Authorized(HttpMethod.Put, path, alice.AccessToken, request)).StatusCode);
        using var scope = factory.Services.CreateScope();
        await OperationsService.SetOperatorAsync(scope.ServiceProvider, "alice@example.test", true);
        Assert.Equal(HttpStatusCode.Forbidden, (await Authorized(HttpMethod.Put, path, alice.AccessToken, request with { CurrentPassword = "wrong" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Authorized(HttpMethod.Put, path, alice.AccessToken, request)).StatusCode);
        var aliceMfa = await Enroll("alice@example.test");
        alice = await LoginMfa("alice@example.test", aliceMfa.Key);
        Assert.Equal(HttpStatusCode.OK, (await Authorized(HttpMethod.Put, path, alice.AccessToken, request)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(alice.AccessToken, target.Id, 1m, "frozen")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Authorized(HttpMethod.Put, path, alice.AccessToken, request with { IsFrozen = false })).StatusCode);
        var bobMfa = await Enroll("bob@example.test");
        bob = await LoginMfa("bob@example.test", bobMfa.Key);
        using (var grantScope = factory.Services.CreateScope()) await OperationsService.SetOperatorAsync(grantScope.ServiceProvider, "bob@example.test", true);
        var approval = await RequestUnfreeze(alice, target.Id);
        Assert.Equal(HttpStatusCode.OK, (await Decide(bob, approval.Id, "approve")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send(alice.AccessToken, target.Id, 1m, "after-unfreeze")).StatusCode);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await db.AuditEvents.CountAsync(x => x.TargetId == target.Id && (x.Action == "Wallet.Frozen" || x.Action == "Wallet.Unfrozen")));
        using var revokeScope = factory.Services.CreateScope();
        await OperationsService.SetOperatorAsync(revokeScope.ServiceProvider, "alice@example.test", false);
        Assert.Equal(HttpStatusCode.Forbidden, (await Authorized(HttpMethod.Put, path, alice.AccessToken, request)).StatusCode);
    }
    [Fact]
    public async Task ReconciliationDetectsProjectionDriftWithoutRepairingIt()
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ReconciliationService>();
        Assert.True((await service.CheckAsync(default)).Healthy);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var walletId = await db.Wallets.Where(x => !x.IsSystem).Select(x => x.Id).FirstAsync();
        await db.Wallets.Where(x => x.Id == walletId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Balance, 10001m));
        var report = await service.CheckAsync(default);
        Assert.False(report.Healthy); Assert.Equal(1, report.BalanceMismatchCount); Assert.Equal(0, report.PostingMismatchCount);
        Assert.Equal(10001m, await db.Wallets.Where(x => x.Id == walletId).Select(x => x.Balance).SingleAsync());
    }
    [Fact]
    public async Task HistoryFiltersAreOwnedAndValidated()
    {
        var alice = await Login("alice@example.test"); var bob = await Login("bob@example.test");
        var target = await Wallet(bob.AccessToken);
        (await Send(alice.AccessToken, target.Id, 2m, "filter")).EnsureSuccessStatusCode();
        var response = await Get("/api/v1/transactions?kind=Transfer&direction=outgoing&from=2000-01-01T00:00:00Z&to=2100-01-01T00:00:00Z", alice.AccessToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("items").GetArrayLength());
        var bobResponse = await Get("/api/v1/transactions?kind=Transfer&direction=outgoing", bob.AccessToken);
        Assert.Equal(0, (await bobResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await Get("/api/v1/transactions?direction=invalid", alice.AccessToken)).StatusCode);
    }
    [Fact]
    public async Task EmailJobRetriesAndClearsSecretPayloadAfterDelivery()
    {
        await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new EmailRequest("alice@example.test"));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var transport = new RecordingTransport { Fail = true };
        var dispatcher = new EmailDispatcher(db, scope.ServiceProvider.GetRequiredService<EmailEnvelope>(), transport,
            Options.Create(new EmailSettings()), scope.ServiceProvider.GetRequiredService<ILogger<EmailDispatcher>>());
        Assert.True(await dispatcher.DispatchOneAsync(default));
        var item = await db.EmailOutbox.AsNoTracking().SingleAsync();
        Assert.Equal(1, item.Attempts); Assert.Null(item.SentAt); Assert.NotEmpty(item.ProtectedPayload);
        Assert.Null(item.LeaseId); Assert.True(item.NextAttemptAt > DateTimeOffset.UtcNow);
        await db.EmailOutbox.ExecuteUpdateAsync(s => s.SetProperty(x => x.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        transport.Fail = false;
        Assert.True(await dispatcher.DispatchOneAsync(default));
        item = await db.EmailOutbox.AsNoTracking().SingleAsync();
        Assert.NotNull(item.SentAt); Assert.Empty(item.ProtectedPayload); Assert.Equal(2, item.Attempts);
        Assert.False(await dispatcher.DispatchOneAsync(default));
    }
    [Fact]
    public async Task FailedLoginCountersPersistAndLockoutIsEnforced()
    {
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("alice@example.test", "wrong-password"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("alice@example.test", "Test-Only-Password-239!"))).StatusCode);
        using var scope = factory.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.SingleAsync(x => x.Email == "alice@example.test");
        Assert.True(user.LockoutEnd > DateTimeOffset.UtcNow);
    }
    [Fact]
    public async Task UpgradePreservesBalancesAndRevokesLegacySessions()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var migrator = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(db);
        // Exercise downgrade/re-upgrade ONLY inside this test's unique disposable database.
        await migrator.MigrateAsync("20261025071227_InitialWalletLedger");
        var actor = await db.Users.Where(x => x.Email == "alice@example.test").Select(x => x.Id).SingleAsync();
        var session = Guid.NewGuid(); var expires = DateTimeOffset.UtcNow.AddDays(1);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [Sessions] ([Id], [UserId], [ExpiresAt]) VALUES ({session}, {actor}, {expires})");
        await migrator.MigrateAsync();
        Assert.Equal(3, await db.Wallets.CountAsync());
        Assert.Equal(2, await db.Wallets.CountAsync(x => !x.IsSystem && x.Balance == 10000));
        Assert.Equal(4, await db.LedgerEntries.CountAsync());
        Assert.NotNull((await db.Sessions.SingleAsync(x => x.Id == session)).RevokedAt);
        Assert.False(db.Database.HasPendingModelChanges());
    }
    [Fact]
    public async Task CompetingEmailWorkersClaimOneJob()
    {
        await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new EmailRequest("alice@example.test"));
        using var first = factory.Services.CreateScope(); using var second = factory.Services.CreateScope();
        var transport = new RecordingTransport();
        EmailDispatcher Dispatcher(IServiceProvider provider) => new(provider.GetRequiredService<AppDbContext>(),
            provider.GetRequiredService<EmailEnvelope>(), transport, Options.Create(new EmailSettings()), provider.GetRequiredService<ILogger<EmailDispatcher>>());
        await Task.WhenAll(Dispatcher(first.ServiceProvider).DispatchOneAsync(default), Dispatcher(second.ServiceProvider).DispatchOneAsync(default));
        Assert.Equal(1, transport.Calls);
        Assert.Equal(1, await first.ServiceProvider.GetRequiredService<AppDbContext>().EmailOutbox.CountAsync(x => x.SentAt != null));
    }
    private sealed class RecordingTransport : IEmailTransport
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public bool Fail { get; set; }
        public Task SendAsync(Guid id, EmailPayload payload, CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            return Fail ? Task.FromException(new IOException("simulated failure")) : Task.CompletedTask;
        }
    }
}
