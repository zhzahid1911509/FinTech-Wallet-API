using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FinTechWalletAPI.Contracts;
using FinTechWalletAPI.Data;
using FinTechWalletAPI.Models;
using FinTechWalletAPI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace FinTechWalletAPI.Tests;

// These tests deliberately use SQL Server, not EF InMemory or SQLite substitutes.
[Trait("Category", "SqlServer")]
public sealed partial class SqlApiTests : IAsyncLifetime
{
    private WalletFactory factory = null!;
    private HttpClient client = null!;
    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("WALLET_TEST_SQL") ??
            throw new InvalidOperationException("Set WALLET_TEST_SQL to a local/test SQL Server connection, or run --filter Category!=SqlServer.");
        var builder = new SqlConnectionStringBuilder(connection) { InitialCatalog = "WalletTests_" + Guid.NewGuid().ToString("N") };
        factory = new WalletFactory(builder.ConnectionString);
        client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
        await DemoSeeder.SeedAsync(scope.ServiceProvider, factory.Services.GetRequiredService<IConfiguration>(), factory.Services.GetRequiredService<IWebHostEnvironment>());
    }
    public async Task DisposeAsync()
    {
        if (factory is null) return;
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureDeletedAsync();
        client.Dispose();
        await factory.DisposeAsync();
    }
    private async Task<TokenResponse> Login(string email, string password = "Test-Only-Password-239!")
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }
    private async Task<HttpResponseMessage> Get(string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }
    private async Task<WalletResponse> Wallet(string token) =>
        (await (await Get("/api/v1/wallets/me", token)).Content.ReadFromJsonAsync<WalletResponse>())!;
    private async Task<HttpResponseMessage> Send(string token, Guid recipient, decimal amount, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transfers") { Content = JsonContent.Create(new TransferRequest(recipient, amount)) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }
    [Fact]
    public async Task RegistrationCreatesZeroBalanceAndRejectsDuplicate()
    {
        var input = new RegisterRequest("new@example.test", "Test-Only-Password-239!");
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/auth/register", input)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/v1/auth/register", input)).StatusCode);
        await ConfirmRegistration(input.Email);
        var token = await Login(input.Email);
        Assert.Equal(0, (await Wallet(token.AccessToken)).Balance);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/wallets/me")).StatusCode);
    }
    [Fact]
    public async Task TransferIsBalancedAndReplayDoesNotChargeTwice()
    {
        var alice = await Login("alice@example.test"); var bob = await Login("bob@example.test");
        var target = await Wallet(bob.AccessToken);
        var first = await Send(alice.AccessToken, target.Id, 500m, "same-key");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var transfer = (await first.Content.ReadFromJsonAsync<TransferResponse>())!;
        var replay = await Send(alice.AccessToken, target.Id, 500m, "same-key");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(transfer.Id, (await replay.Content.ReadFromJsonAsync<TransferResponse>())!.Id);
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(HttpStatusCode.Conflict, (await Send(alice.AccessToken, target.Id, 501m, "same-key")).StatusCode);
        Assert.Equal(9500, (await Wallet(alice.AccessToken)).Balance);
        Assert.Equal(10500, (await Wallet(bob.AccessToken)).Balance);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entries = await db.LedgerEntries.Where(x => x.TransferId == transfer.Id).ToListAsync();
        Assert.Equal(2, entries.Count); Assert.Equal(entries.Sum(x => x.Debit), entries.Sum(x => x.Credit));
        foreach (var wallet in await db.Wallets.ToListAsync())
            Assert.Equal(wallet.Balance, await db.LedgerEntries.Where(x => x.WalletId == wallet.Id).SumAsync(x => x.Credit - x.Debit));
    }
    [Fact]
    public async Task ConcurrentSpendsCannotOverdraw()
    {
        var alice = await Login("alice@example.test"); var bob = await Login("bob@example.test");
        var target = await Wallet(bob.AccessToken);
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Send(alice.AccessToken, target.Id, 7000m, "parallel-" + i)));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Created);
        Assert.All(responses.Where(x => x.StatusCode != HttpStatusCode.Created), x => Assert.Contains(x.StatusCode,
            new[] { HttpStatusCode.UnprocessableEntity, HttpStatusCode.Conflict }));
        Assert.Equal(3000, (await Wallet(alice.AccessToken)).Balance);
        Assert.Equal(17000, (await Wallet(bob.AccessToken)).Balance);
    }
    [Fact]
    public async Task ConcurrentDuplicateRequestsCreateOnePosting()
    {
        var alice = await Login("alice@example.test"); var bob = await Login("bob@example.test");
        var target = await Wallet(bob.AccessToken);
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Send(alice.AccessToken, target.Id, 100m, "duplicate-key")));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Created);
        Assert.All(responses, x => Assert.Contains(x.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK, HttpStatusCode.Conflict }));
        Assert.Equal(HttpStatusCode.OK, (await Send(alice.AccessToken, target.Id, 100m, "duplicate-key")).StatusCode);
        Assert.Equal(9900, (await Wallet(alice.AccessToken)).Balance);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Transfers.CountAsync(x => x.IdempotencyKey == "duplicate-key"));
    }
    [Fact]
    public async Task InvalidTransfersLeaveBalancesUntouched()
    {
        var alice = await Login("alice@example.test"); var bob = await Login("bob@example.test");
        var source = await Wallet(alice.AccessToken); var target = await Wallet(bob.AccessToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Send(alice.AccessToken, target.Id, 10001m, "too-much")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(alice.AccessToken, target.Id, 0.001m, "fraction")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(alice.AccessToken, source.Id, 10m, "self")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(alice.AccessToken, Guid.NewGuid(), 10m, "missing")).StatusCode);
        Assert.Equal(10000, (await Wallet(alice.AccessToken)).Balance);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Transfers.CountAsync());
    }
    [Fact]
    public async Task OutsiderCannotReadTransfer()
    {
        var alice = await Login("alice@example.test"); var bob = await Login("bob@example.test");
        var target = await Wallet(bob.AccessToken);
        var transfer = (await (await Send(alice.AccessToken, target.Id, 1m, "private")).Content.ReadFromJsonAsync<TransferResponse>())!;
        (await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest("eve@example.test", "Test-Only-Password-239!"))).EnsureSuccessStatusCode();
        await ConfirmRegistration("eve@example.test");
        var eve = await Login("eve@example.test");
        Assert.Equal(HttpStatusCode.NotFound, (await Get($"/api/v1/transfers/{transfer.Id}", eve.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get($"/api/v1/transfers/{transfer.Id}", bob.AccessToken)).StatusCode);
    }
    [Fact]
    public async Task RefreshReplayRevokesSessionAndNewAccessToken()
    {
        var alice = await Login("alice@example.test");
        var rotated = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(alice.RefreshToken));
        rotated.EnsureSuccessStatusCode();
        var next = (await rotated.Content.ReadFromJsonAsync<TokenResponse>())!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(alice.RefreshToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/api/v1/wallets/me", next.AccessToken)).StatusCode);
    }
    [Fact]
    public async Task LogoutRevokesAccessAndRefresh()
    {
        var alice = await Login("alice@example.test");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", alice.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(request)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/api/v1/wallets/me", alice.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(alice.RefreshToken))).StatusCode);
    }
    [Fact]
    public async Task DatabaseFailureRollsBackBothBalancesAndIdempotencyRecord()
    {
        var alice = await Login("alice@example.test"); var bob = await Login("bob@example.test");
        var target = await Wallet(bob.AccessToken);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE LedgerEntries ADD CONSTRAINT CK_TestFailure CHECK ([Debit] <> 123 AND [Credit] <> 123)");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Send(alice.AccessToken, target.Id, 123m, "rollback")).StatusCode);
        Assert.Equal(10000, (await Wallet(alice.AccessToken)).Balance);
        Assert.Equal(10000, (await Wallet(bob.AccessToken)).Balance);
        Assert.False(await db.Transfers.AnyAsync(x => x.IdempotencyKey == "rollback"));
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE LedgerEntries DROP CONSTRAINT CK_TestFailure");
        Assert.Equal(HttpStatusCode.Created, (await Send(alice.AccessToken, target.Id, 123m, "rollback")).StatusCode);
    }
    private sealed class WalletFactory(string connection) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Email:WorkerEnabled", "false");
            builder.UseSetting("ConnectionStrings:DefaultConnection", connection);
            builder.UseSetting("Jwt:SigningKey", "integration-tests-only-not-a-production-key-239!");
            builder.UseSetting("Demo:Password", "Test-Only-Password-239!");
        }
    }
}
