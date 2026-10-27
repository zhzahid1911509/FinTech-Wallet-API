using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FinTechWalletAPI.Contracts;
using FinTechWalletAPI.Data;
using FinTechWalletAPI.Models;
using FinTechWalletAPI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
namespace FinTechWalletAPI.Tests;
public sealed partial class SqlApiTests
{
    private sealed class CheckStub(Func<CancellationToken, Task<ReconciliationReport>> run) : IReconciliationCheck
    { public Task<ReconciliationReport> CheckAsync(CancellationToken ct) => run(ct); }
    private ReconciliationScheduler Scheduler(IServiceProvider sp, IReconciliationCheck? check = null) => new(
        sp.GetRequiredService<AppDbContext>(), check ?? sp.GetRequiredService<IReconciliationCheck>(),
        sp.GetRequiredService<IOptions<ReconciliationSettings>>(), factory.Clock, sp.GetRequiredService<ILogger<ReconciliationScheduler>>());
    private static ReconciliationReport HealthyReport() => new(DateTimeOffset.UtcNow,true,0,0,[],[]);
    private async Task<JsonElement> Monitoring(TokenResponse user)
    {
        var response = await Get("/api/v1/operations/monitoring",user.AccessToken); response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
    [Fact]
    public async Task ScheduledHealthyRunPersistsAndDoesNotRepeatBeforeDue()
    {
        using var scope=factory.Services.CreateScope(); var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scheduler=Scheduler(scope.ServiceProvider);
        var id=await scheduler.RunDueAsync(default); Assert.NotNull(id);
        var run=await db.ReconciliationRuns.AsNoTracking().SingleAsync();
        Assert.Equal("Healthy",run.Status); Assert.Equal(0,run.BalanceMismatchCount); Assert.NotNull(run.ReportJson);
        Assert.True(await db.AuditEvents.AnyAsync(x=>x.Action=="Reconciliation.Healthy" && x.ActorId==Guid.Empty && x.TargetId==id));
        Assert.Null(await scheduler.RunDueAsync(default));
        factory.Clock.Advance(TimeSpan.FromSeconds(901));
        Assert.NotNull(await scheduler.RunDueAsync(default)); Assert.Equal(2,await db.ReconciliationRuns.CountAsync());
        Assert.Equal(2,await db.Transfers.CountAsync()); Assert.Equal(4,await db.LedgerEntries.CountAsync());
    }
    [Fact]
    public async Task UnhealthyRunDetectsMismatchWithoutRepairingBalance()
    {
        using var scope=factory.Services.CreateScope(); var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var wallet=await db.Wallets.FirstAsync(x=>!x.IsSystem);
        await db.Wallets.Where(x=>x.Id==wallet.Id).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Balance,10001m));
        var id=await Scheduler(scope.ServiceProvider).RunDueAsync(default);
        var run=await db.ReconciliationRuns.AsNoTracking().SingleAsync();
        Assert.Equal("Unhealthy",run.Status); Assert.Equal(1,run.BalanceMismatchCount);
        Assert.Equal(10001m,await db.Wallets.Where(x=>x.Id==wallet.Id).Select(x=>x.Balance).SingleAsync());
        await GrantOperator("alice@example.test"); var alice=await Login("alice@example.test");
        var summary=await Monitoring(alice); Assert.Contains("reconciliation_unhealthy",summary.GetProperty("alerts").EnumerateArray().Select(x=>x.GetString()));
        var detail=await Get($"/api/v1/operations/reconciliation-runs/{id}",alice.AccessToken); detail.EnsureSuccessStatusCode();
        Assert.Equal(1,(await detail.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("report").GetProperty("balanceMismatchCount").GetInt32());
    }
    [Fact]
    public async Task CompetingSchedulersOnlyClaimOnce()
    {
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var first=factory.Services.CreateScope(); using var second=factory.Services.CreateScope();
        var task=Scheduler(first.ServiceProvider,new CheckStub(async ct=>{started.SetResult();await release.Task.WaitAsync(ct);return HealthyReport();})).RunDueAsync(default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { Assert.Null(await Scheduler(second.ServiceProvider).RunDueAsync(default)); }
        finally { release.SetResult(); }
        Assert.NotNull(await task);
        Assert.Equal(1,await second.ServiceProvider.GetRequiredService<AppDbContext>().ReconciliationRuns.CountAsync());
    }
    [Fact]
    public async Task ExpiredLeaseRecoversAndFencesLateResult()
    {
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var first=factory.Services.CreateScope(); using var second=factory.Services.CreateScope();
        var task=Scheduler(first.ServiceProvider,new CheckStub(async ct=>{started.SetResult();await release.Task.WaitAsync(ct);return new(DateTimeOffset.UtcNow,false,99,99,[],[]);})).RunDueAsync(default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Guid? next;
        try { factory.Clock.Advance(TimeSpan.FromSeconds(91)); next=await Scheduler(second.ServiceProvider).RunDueAsync(default); Assert.NotNull(next); }
        finally { release.SetResult(); }
        var old=await task;
        var db=second.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal("Abandoned",await db.ReconciliationRuns.Where(x=>x.Id==old).Select(x=>x.Status).SingleAsync());
        Assert.Equal("Healthy",await db.ReconciliationRuns.Where(x=>x.Id==next).Select(x=>x.Status).SingleAsync());
        Assert.False(await db.ReconciliationRuns.AnyAsync(x=>x.BalanceMismatchCount==99));
    }
    [Fact]
    public async Task FailedCheckStoresOnlyExceptionTypeAndRetriesOnSchedule()
    {
        using var scope=factory.Services.CreateScope(); var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var id=await Scheduler(scope.ServiceProvider,new CheckStub(_=>throw new IOException("PRIVATE-CREDENTIAL-DO-NOT-LOG"))).RunDueAsync(default);
        var run=await db.ReconciliationRuns.AsNoTracking().SingleAsync();
        Assert.Equal("Failed",run.Status); Assert.Equal("IOException",run.ErrorType); Assert.Null(run.ReportJson);
        Assert.Null(await Scheduler(scope.ServiceProvider).RunDueAsync(default));
        await GrantOperator("alice@example.test"); var alice=await Login("alice@example.test");
        var detail=await Get($"/api/v1/operations/reconciliation-runs/{id}",alice.AccessToken);
        Assert.DoesNotContain("PRIVATE-CREDENTIAL",await detail.Content.ReadAsStringAsync());
        factory.Clock.Advance(TimeSpan.FromSeconds(901));
        Assert.NotNull(await Scheduler(scope.ServiceProvider).RunDueAsync(default));
    }
    [Fact]
    public async Task ScheduledCheckTimeoutIsRecordedAsFailure()
    {
        using var scope=factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IOptions<ReconciliationSettings>>().Value.TimeoutSeconds=5;
        var id=await Scheduler(scope.ServiceProvider,new CheckStub(async ct=>{await Task.Delay(Timeout.Infinite,ct);return HealthyReport();})).RunDueAsync(default);
        var run=await scope.ServiceProvider.GetRequiredService<AppDbContext>().ReconciliationRuns.AsNoTracking().SingleAsync();
        Assert.Equal("Failed",run.Status); Assert.Equal("CancelledOrTimedOut",run.ErrorType); Assert.NotNull(run.FinishedAt);
    }
    [Fact]
    public async Task CompletionAndAuditCommitAtomically()
    {
        using(var scope=factory.Services.CreateScope()) {
            var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE AuditEvents ADD CONSTRAINT CK_TestRunAudit CHECK ([Action] <> 'Reconciliation.Healthy')");
            await Assert.ThrowsAsync<DbUpdateException>(()=>Scheduler(scope.ServiceProvider).RunDueAsync(default));
            Assert.Equal("Running",await db.ReconciliationRuns.AsNoTracking().Select(x=>x.Status).SingleAsync());
            Assert.NotNull((await db.ReconciliationSchedules.AsNoTracking().SingleAsync()).LeaseId);
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE AuditEvents DROP CONSTRAINT CK_TestRunAudit");
        }
        factory.Clock.Advance(TimeSpan.FromSeconds(91));
        using var retry=factory.Services.CreateScope();
        Assert.NotNull(await Scheduler(retry.ServiceProvider).RunDueAsync(default));
        var statuses=await retry.ServiceProvider.GetRequiredService<AppDbContext>().ReconciliationRuns.Select(x=>x.Status).ToListAsync();
        Assert.Contains("Healthy",statuses); Assert.Contains("Abandoned",statuses);
    }
    [Fact]
    public async Task MonitoringReportsNeverRunAndStaleResults()
    {
        await GrantOperator("alice@example.test"); var alice=await Login("alice@example.test");
        Assert.Contains("reconciliation_never_completed",(await Monitoring(alice)).GetProperty("alerts").EnumerateArray().Select(x=>x.GetString()));
        using var scope=factory.Services.CreateScope(); await Scheduler(scope.ServiceProvider).RunDueAsync(default);
        factory.Clock.Advance(TimeSpan.FromSeconds(3601));
        Assert.Contains("reconciliation_stale",(await Monitoring(alice)).GetProperty("alerts").EnumerateArray().Select(x=>x.GetString()));
    }
    [Theory]
    [InlineData("/api/v1/operations/monitoring")]
    [InlineData("/api/v1/operations/audit-events")]
    [InlineData("/api/v1/operations/reconciliation-runs")]
    [InlineData("/api/v1/operations/email-jobs")]
    public async Task MonitoringRequiresCurrentOperatorRole(string path)
    {
        var alice=await Login("alice@example.test");
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await Get(path,alice.AccessToken)).StatusCode);
        await GrantOperator("alice@example.test"); Assert.Equal(HttpStatusCode.OK,(await Get(path,alice.AccessToken)).StatusCode);
        using var scope=factory.Services.CreateScope(); await OperationsService.SetOperatorAsync(scope.ServiceProvider,"alice@example.test",false);
        Assert.Equal(HttpStatusCode.Forbidden,(await Get(path,alice.AccessToken)).StatusCode);
    }
    [Fact]
    public async Task AuditFiltersArePaginatedAndValidateInputs()
    {
        await GrantOperator("alice@example.test"); var alice=await Login("alice@example.test");
        using var scope=factory.Services.CreateScope(); var db=scope.ServiceProvider.GetRequiredService<AppDbContext>(); var actor=Guid.NewGuid();
        db.AuditEvents.AddRange(Enumerable.Range(0,3).Select(_=>new AuditEvent {ActorId=actor,TargetId=Guid.NewGuid(),Action="Test.Filter"})); await db.SaveChangesAsync();
        var response=await Get($"/api/v1/operations/audit-events?action=Test.Filter&actorId={actor}&pageSize=2",alice.AccessToken);
        var json=await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("hasMore").GetBoolean()); Assert.Equal(2,json.GetProperty("items").GetArrayLength());
        Assert.All(json.GetProperty("items").EnumerateArray(),x=>Assert.Equal(actor,x.GetProperty("actorId").GetGuid()));
        Assert.Equal(HttpStatusCode.BadRequest,(await Get("/api/v1/operations/audit-events?pageSize=101",alice.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await Get("/api/v1/operations/audit-events?from=2026-10-27T00:00:00Z&to=2026-10-26T00:00:00Z",alice.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await Get($"/api/v1/operations/reconciliation-runs/{Guid.NewGuid()}",alice.AccessToken)).StatusCode);
    }
    [Fact]
    public async Task EmailInspectionRedactsPayloadAndExposesFailureBacklogSignals()
    {
        await GrantOperator("alice@example.test"); var alice=await Login("alice@example.test");
        using var scope=factory.Services.CreateScope(); var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();var now=factory.Clock.GetUtcNow();
        db.EmailOutbox.AddRange(new EmailOutbox{FailedAt=now,ExpiresAt=now.AddHours(1),ProtectedPayload="PRIVATE-EMAIL-TOKEN",LastErrorType="IOException"},
            new EmailOutbox{ExpiresAt=now.AddHours(1),CreatedAt=now.AddMinutes(-20),ProtectedPayload="PRIVATE-EMAIL-TOKEN"},
            new EmailOutbox{ExpiresAt=now.AddMinutes(-1),ProtectedPayload="PRIVATE-EMAIL-TOKEN"});await db.SaveChangesAsync();
        var response=await Get("/api/v1/operations/email-jobs?state=failed",alice.AccessToken);var text=await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("PRIVATE",text);Assert.DoesNotContain("protectedPayload",text);Assert.Contains("IOException",text);
        var summary=await Monitoring(alice);var alerts=summary.GetProperty("alerts").EnumerateArray().Select(x=>x.GetString()).ToArray();
        Assert.Contains("email_failed",alerts);Assert.Contains("email_expired",alerts);Assert.Contains("email_backlog",alerts);
        Assert.Equal(HttpStatusCode.BadRequest,(await Get("/api/v1/operations/email-jobs?state=anything",alice.AccessToken)).StatusCode);
        factory.Clock.Advance(TimeSpan.FromHours(24).Add(TimeSpan.FromSeconds(1)));
        var later=await Monitoring(alice);
        Assert.Equal(1,later.GetProperty("email").GetProperty("failed").GetInt32());
        Assert.Equal(0,later.GetProperty("email").GetProperty("failedLast24Hours").GetInt32());
        Assert.DoesNotContain("email_failed",later.GetProperty("alerts").EnumerateArray().Select(x=>x.GetString()));
    }
    [Fact]
    public async Task KnownAccountLoginFailuresAreAuditedWithoutCredentials()
    {
        var response=await client.PostAsJsonAsync("/api/v1/auth/login",new LoginRequest("alice@example.test","PRIVATE-WRONG-PASSWORD"));
        Assert.Equal(HttpStatusCode.Unauthorized,response.StatusCode);
        using var scope=factory.Services.CreateScope(); var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audit=await db.AuditEvents.SingleAsync(x=>x.Action=="Auth.LoginFailed");Assert.Null(audit.Reason);
        await GrantOperator("bob@example.test");var bob=await Login("bob@example.test");
        Assert.Equal(1,(await Monitoring(bob)).GetProperty("securityLast24Hours").GetProperty("loginFailures").GetInt32());
    }
    [Fact]
    public async Task MonitoringMigrationPreservesExistingWalletAndSessionData()
    {
        var alice=await Login("alice@example.test");
        using var scope=factory.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var previous=db.Database.GetMigrations().Single(x=>x.EndsWith("_RiskBasedTransferAuthorization"));
        await db.GetService<IMigrator>().MigrateAsync(previous);await db.Database.MigrateAsync();
        Assert.Equal(2,await db.Wallets.CountAsync(x=>!x.IsSystem && x.Balance==10000m));
        Assert.Equal(4,await db.LedgerEntries.CountAsync());Assert.True(await db.Sessions.AnyAsync(x=>x.Id==alice.SessionId && x.RevokedAt==null));
        Assert.Equal(1,await db.ReconciliationSchedules.CountAsync());Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(HttpStatusCode.OK,(await Get("/api/v1/wallets/me",alice.AccessToken)).StatusCode);
    }
}
