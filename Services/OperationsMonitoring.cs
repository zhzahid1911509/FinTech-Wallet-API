using System.Text.Json;
using FinTechWalletAPI.Data;
using FinTechWalletAPI.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
namespace FinTechWalletAPI.Services;
public sealed class OperationsMonitoring(AppDbContext db, TimeProvider clock, IOptions<ReconciliationSettings> options)
{
    public static (int Page, int Size) ValidatePage(int? page, int? pageSize)
    {
        var p = page ?? 1; var size = pageSize ?? 20;
        if (p < 1 || p > 100000 || size < 1 || size > 100) throw new ApiException(400, "invalid_pagination", "Page must be 1–100000; pageSize must be 1–100.");
        return (p, size);
    }
    public async Task<object> SummaryAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow(); var since = now.AddHours(-24); var overdue = now.AddMinutes(-options.Value.EmailBacklogMinutes);
        var latest = await db.ReconciliationRuns.AsNoTracking().Where(x => x.Status != "Running").OrderByDescending(x => x.StartedAt).ThenByDescending(x => x.Id)
            .Select(x => new { x.Id, x.Status, x.StartedAt, x.FinishedAt, x.BalanceMismatchCount, x.PostingMismatchCount, x.ErrorType }).FirstOrDefaultAsync(ct);
        var schedule = await db.ReconciliationSchedules.AsNoTracking().SingleAsync(x => x.Id == 1, ct);
        var pending = db.EmailOutbox.Where(x => x.SentAt == null && x.FailedAt == null);
        var failed = await db.EmailOutbox.CountAsync(x => x.FailedAt != null, ct);
        var failedLast24Hours = await db.EmailOutbox.CountAsync(x => x.FailedAt >= since, ct);
        var expired = await pending.CountAsync(x => x.ExpiresAt <= now, ct);
        var backlog = await pending.CountAsync(x => x.ExpiresAt > now && x.CreatedAt <= overdue, ct);
        var alerts = new List<string>();
        if (!options.Value.WorkerEnabled) alerts.Add("reconciliation_worker_disabled");
        if (latest is null) alerts.Add("reconciliation_never_completed");
        else {
            if (latest.Status != "Healthy") alerts.Add("reconciliation_" + latest.Status.ToLowerInvariant());
            if (latest.FinishedAt <= now.AddSeconds(-options.Value.StaleAfterSeconds)) alerts.Add("reconciliation_stale");
        }
        if (schedule.LeaseId != null && schedule.LeaseUntil <= now) alerts.Add("reconciliation_lease_expired");
        if (failedLast24Hours > 0) alerts.Add("email_failed");
        if (expired > 0) alerts.Add("email_expired");
        if (backlog > 0) alerts.Add("email_backlog");
        return new { checkedAt = now, attentionRequired = alerts.Count != 0, alerts,
            reconciliation = new { workerEnabled = options.Value.WorkerEnabled, schedule.NextRunAt, running = schedule.LeaseId != null && schedule.LeaseUntil > now, schedule.LeaseUntil, latest },
            email = new { pending = await pending.CountAsync(x => x.ExpiresAt > now, ct), failed, failedLast24Hours, expired, overdue = backlog },
            securityLast24Hours = new {
                loginFailures = await db.AuditEvents.CountAsync(x => x.CreatedAt >= since && x.Action == "Auth.LoginFailed", ct),
                mfaFailures = await db.AuditEvents.CountAsync(x => x.CreatedAt >= since && x.Action == "Mfa.VerificationFailed", ct),
                transferRiskDenials = await db.AuditEvents.CountAsync(x => x.CreatedAt >= since && x.Action == "Transfer.RiskDenied", ct),
                refreshReplays = await db.AuditEvents.CountAsync(x => x.CreatedAt >= since && x.Action == "Session.RefreshReplay", ct)
            } };
    }
    public async Task<object> AuditAsync(int? page, int? pageSize, DateTimeOffset? from, DateTimeOffset? to, string? action, Guid? actorId, Guid? targetId, CancellationToken ct)
    {
        var (p, size) = ValidatePage(page, pageSize);
        if (from.HasValue && to.HasValue && from >= to) throw new ApiException(400, "invalid_date_range", "from must precede the exclusive to value.");
        if (action is not null && (string.IsNullOrWhiteSpace(action) || action.Length > 64)) throw new ApiException(400, "invalid_action", "Action must contain 1–64 characters.");
        var q = db.AuditEvents.AsNoTracking();
        if (from.HasValue) q = q.Where(x => x.CreatedAt >= from);
        if (to.HasValue) q = q.Where(x => x.CreatedAt < to);
        if (action is not null) q = q.Where(x => x.Action == action);
        if (actorId.HasValue) q = q.Where(x => x.ActorId == actorId);
        if (targetId.HasValue) q = q.Where(x => x.TargetId == targetId);
        var items = await q.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Skip((p - 1) * size).Take(size + 1)
            .Select(x => new { x.Id, x.ActorId, x.TargetId, x.Action, x.Reason, x.CreatedAt }).ToListAsync(ct);
        return new { page = p, pageSize = size, hasMore = items.Count > size, items = items.Take(size) };
    }
    public async Task<object> RunsAsync(int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = ValidatePage(page, pageSize);
        var items = await db.ReconciliationRuns.AsNoTracking().OrderByDescending(x => x.StartedAt).ThenByDescending(x => x.Id)
            .Skip((p - 1) * size).Take(size + 1).Select(x => new { x.Id, x.Status, x.StartedAt, x.FinishedAt, x.BalanceMismatchCount, x.PostingMismatchCount, x.ErrorType }).ToListAsync(ct);
        return new { page = p, pageSize = size, hasMore = items.Count > size, items = items.Take(size) };
    }
    public async Task<object> RunAsync(Guid id, CancellationToken ct)
    {
        var run = await db.ReconciliationRuns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new ApiException(404, "reconciliation_run_not_found", "Reconciliation run was not found.");
        return new { run.Id, run.Status, run.StartedAt, run.FinishedAt, run.ErrorType,
            report = run.ReportJson is null ? null : JsonSerializer.Deserialize<ReconciliationReport>(run.ReportJson) };
    }
    public async Task<object> EmailJobsAsync(string? state, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = ValidatePage(page, pageSize); state ??= "failed";
        if (state is not ("failed" or "pending" or "expired" or "sent")) throw new ApiException(400, "invalid_email_state", "Use failed, pending, expired or sent.");
        var now = clock.GetUtcNow(); var q = db.EmailOutbox.AsNoTracking();
        q = state switch {
            "failed" => q.Where(x => x.FailedAt != null),
            "sent" => q.Where(x => x.SentAt != null),
            "expired" => q.Where(x => x.SentAt == null && x.FailedAt == null && x.ExpiresAt <= now),
            _ => q.Where(x => x.SentAt == null && x.FailedAt == null && x.ExpiresAt > now)
        };
        var items = await q.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Skip((p - 1) * size).Take(size + 1)
            .Select(x => new { x.Id, x.CreatedAt, x.ExpiresAt, x.Attempts, x.NextAttemptAt, x.SentAt, x.FailedAt, x.LastErrorType }).ToListAsync(ct);
        return new { state, page = p, pageSize = size, hasMore = items.Count > size, items = items.Take(size) };
    }
}
