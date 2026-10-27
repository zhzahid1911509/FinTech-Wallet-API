using System.Data;
using FinTechWalletAPI.Data;
using Microsoft.EntityFrameworkCore;
namespace FinTechWalletAPI.Services;
public sealed record BalanceMismatch(Guid WalletId, string Currency, decimal Balance, decimal LedgerBalance);
public sealed record ReconciliationReport(DateTimeOffset CheckedAt, bool Healthy, int BalanceMismatchCount,
    int PostingMismatchCount, IReadOnlyList<BalanceMismatch> BalanceMismatches, IReadOnlyList<Guid> PostingMismatches);
public interface IReconciliationCheck
{ Task<ReconciliationReport> CheckAsync(CancellationToken ct); }
public sealed class ReconciliationService(AppDbContext db) : IReconciliationCheck
{
    public async Task<ReconciliationReport> CheckAsync(CancellationToken ct)
    {
        // A consistent read across all queries. This is read-only and may block writers; use off-peak for large ledgers.
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var balances = db.Wallets.AsNoTracking().Select(w => new {
            w.Id, w.Currency, w.Balance,
            Ledger = db.LedgerEntries.Where(e => e.WalletId == w.Id).Sum(e => (decimal?)(e.Credit - e.Debit)) ?? 0m
        }).Where(w => w.Balance != w.Ledger);
        var badPostings = db.Transfers.AsNoTracking().Where(t =>
            db.LedgerEntries.Count(e => e.TransferId == t.Id) != 2 ||
            db.LedgerEntries.Count(e => e.TransferId == t.Id && e.WalletId == t.SourceWalletId && e.Debit == t.Amount && e.Credit == 0 && e.Currency == t.Currency) != 1 ||
            db.LedgerEntries.Count(e => e.TransferId == t.Id && e.WalletId == t.DestinationWalletId && e.Credit == t.Amount && e.Debit == 0 && e.Currency == t.Currency) != 1);
        var balanceCount = await balances.CountAsync(ct);
        var postingCount = await badPostings.CountAsync(ct);
        var balanceDetails = await balances.OrderBy(w => w.Id).Take(100)
            .Select(w => new BalanceMismatch(w.Id, w.Currency, w.Balance, w.Ledger)).ToListAsync(ct);
        var postingDetails = await badPostings.OrderBy(t => t.Id).Select(t => t.Id).Take(100).ToListAsync(ct);
        await tx.CommitAsync(ct);
        return new(DateTimeOffset.UtcNow, balanceCount == 0 && postingCount == 0, balanceCount, postingCount, balanceDetails, postingDetails);
    }
}
