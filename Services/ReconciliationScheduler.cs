using System.Text.Json;
using FinTechWalletAPI.Data;
using FinTechWalletAPI.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
namespace FinTechWalletAPI.Services;
public sealed class ReconciliationSettings
{
    public bool WorkerEnabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 900;
    public int TimeoutSeconds { get; set; } = 60;
    public int StaleAfterSeconds { get; set; } = 3600;
    public int EmailBacklogMinutes { get; set; } = 15;
    public bool IsValid() => IntervalSeconds is >= 60 and <= 86400 && TimeoutSeconds is >= 5 and <= 300 &&
        StaleAfterSeconds >= IntervalSeconds + TimeoutSeconds + 30 && StaleAfterSeconds <= 172800 && EmailBacklogMinutes is >= 1 and <= 1440;
}
public sealed class ReconciliationScheduler(AppDbContext db, IReconciliationCheck check,
    IOptions<ReconciliationSettings> options, TimeProvider clock, ILogger<ReconciliationScheduler> logger)
{
    private readonly ReconciliationSettings settings = options.Value;
    public async Task<Guid?> RunDueAsync(CancellationToken ct)
    {
        var id = await ClaimAsync(ct);
        if (id is null) return null;
        ReconciliationReport? report = null;
        string? error = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        try { report = await check.CheckAsync(deadline.Token); }
        catch (Exception ex) { error = ex is OperationCanceledException ? "CancelledOrTimedOut" : ex.GetType().Name; }
        // Attempt to record failure even during shutdown. A dead process is recovered by lease expiry.
        using var finalize = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (!await FinishAsync(id.Value, report, error, finalize.Token))
            logger.LogWarning("Reconciliation run {RunId} lost its lease; result was not published", id);
        else if (report?.Healthy == true) logger.LogInformation("Reconciliation run {RunId} completed healthy", id);
        else logger.LogWarning("Reconciliation run {RunId} needs attention: {Status}; balances {BalanceCount}; postings {PostingCount}; error {ErrorType}",
            id, report is null ? "Failed" : "Unhealthy", report?.BalanceMismatchCount, report?.PostingMismatchCount, error);
        return id;
    }
    private async Task<Guid?> ClaimAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow(); var id = Guid.NewGuid();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var changed = await db.ReconciliationSchedules.Where(x => x.Id == 1 && x.NextRunAt <= now && (x.LeaseUntil == null || x.LeaseUntil <= now))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseId, id)
                .SetProperty(x => x.LeaseUntil, now.AddSeconds(settings.TimeoutSeconds + 30)), ct);
        if (changed == 0) return null;
        // Only an expired owner's unfinished runs can remain here. A new token fences old writers.
        await db.ReconciliationRuns.Where(x => x.Status == "Running")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Abandoned").SetProperty(x => x.FinishedAt, now)
                .SetProperty(x => x.ErrorType, "LeaseExpired"), ct);
        db.ReconciliationRuns.Add(new ReconciliationRun { Id = id, StartedAt = now });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return id;
    }
    public async Task<bool> FinishAsync(Guid id, ReconciliationReport? report, string? error, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var changed = await db.ReconciliationSchedules.Where(x => x.Id == 1 && x.LeaseId == id && x.LeaseUntil > now)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseId, (Guid?)null).SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(x => x.NextRunAt, now.AddSeconds(settings.IntervalSeconds)), ct);
        if (changed == 0) return false;
        var status = report is null ? "Failed" : report.Healthy ? "Healthy" : "Unhealthy";
        var json = report is null ? null : JsonSerializer.Serialize(report);
        var count = await db.ReconciliationRuns.Where(x => x.Id == id && x.Status == "Running")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status).SetProperty(x => x.FinishedAt, now)
                .SetProperty(x => x.BalanceMismatchCount, report == null ? (int?)null : report.BalanceMismatchCount)
                .SetProperty(x => x.PostingMismatchCount, report == null ? (int?)null : report.PostingMismatchCount)
                .SetProperty(x => x.ReportJson, json).SetProperty(x => x.ErrorType, error), ct);
        if (count != 1) throw new InvalidOperationException("Reconciliation run missing during completion.");
        db.AuditEvents.Add(new AuditEvent { ActorId = Guid.Empty, TargetId = id, Action = "Reconciliation." + status, CreatedAt = now });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return true;
    }
}
public sealed class ReconciliationWorker(IServiceScopeFactory scopes, IOptions<ReconciliationSettings> options,
    ILogger<ReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.WorkerEnabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ReconciliationScheduler>().RunDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning("Scheduled reconciliation unavailable ({ErrorType})", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
