using FinTechWalletAPI.Data;
using FinTechWalletAPI.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace FinTechWalletAPI.Tests;
public sealed class FinancialInvariantTests
{
    private static AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlServer("Server=localhost;Database=NeverConnect;Integrated Security=True").Options);
    [Fact]
    public async Task UnbalancedPostingFailsBeforeDatabaseWrite()
    {
        await using var db = Context();
        var transfer = new Transfer { Amount = 100m, SourceWalletId = Guid.NewGuid(), DestinationWalletId = Guid.NewGuid() };
        db.Transfers.Add(transfer);
        db.LedgerEntries.Add(new LedgerEntry { TransferId = transfer.Id, WalletId = transfer.SourceWalletId, Debit = 100m });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("matching debit and credit", error.Message);
    }
    [Fact]
    public async Task LedgerCannotAppendToPreviouslyPostedTransfer()
    {
        await using var db = Context();
        db.LedgerEntries.Add(new LedgerEntry { TransferId = Guid.NewGuid(), WalletId = Guid.NewGuid(), Credit = 100m });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("with their transfer", error.Message);
    }
    [Fact]
    public async Task PostedTransferCannotBeEdited()
    {
        await using var db = Context();
        var transfer = new Transfer { Amount = 10m };
        db.Attach(transfer); transfer.Amount = 20m;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("append-only", error.Message);
    }
    [Fact]
    public async Task LedgerCannotBeDeleted()
    {
        await using var db = Context();
        var entry = new LedgerEntry { Credit = 10m };
        db.Attach(entry); db.Remove(entry);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("append-only", error.Message);
    }
}
