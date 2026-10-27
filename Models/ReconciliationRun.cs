namespace FinTechWalletAPI.Models;
public sealed class ReconciliationSchedule
{
    public int Id { get; set; } = 1;
    public DateTimeOffset NextRunAt { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
}
public sealed class ReconciliationRun
{
    public Guid Id { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string Status { get; set; } = "Running";
    public int? BalanceMismatchCount { get; set; }
    public int? PostingMismatchCount { get; set; }
    public string? ReportJson { get; set; }
    public string? ErrorType { get; set; }
}
