using FinTechWalletAPI.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
namespace FinTechWalletAPI.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<User, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<Transfer> Transfers => Set<Transfer>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<AuthSession> Sessions => Set<AuthSession>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AccountChallenge> AccountChallenges => Set<AccountChallenge>();
    public DbSet<EmailOutbox> EmailOutbox => Set<EmailOutbox>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.Entity<User>().HasIndex(x => x.NormalizedEmail).IsUnique();
        b.Entity<Wallet>(e => {
            e.HasIndex(x => x.UserId).IsUnique().HasFilter("[UserId] IS NOT NULL");
            e.HasOne(x => x.User).WithOne().HasForeignKey<Wallet>(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Currency).HasMaxLength(3).IsUnicode(false);
            e.Property(x => x.Balance).HasPrecision(19, 2);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.ToTable("Wallets", t => {
                t.HasCheckConstraint("CK_Wallet_Balance", "[IsSystem] = 1 OR [Balance] >= 0");
                t.HasCheckConstraint("CK_Wallet_Owner", "([IsSystem] = 1 AND [UserId] IS NULL) OR ([IsSystem] = 0 AND [UserId] IS NOT NULL)");
                t.HasCheckConstraint("CK_Wallet_Currency", "[Currency] = 'BDT'");
            });
        });
        b.Entity<Transfer>(e => {
            e.HasIndex(x => new { x.ActorId, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => new { x.SourceWalletId, x.CreatedAt });
            e.HasIndex(x => new { x.DestinationWalletId, x.CreatedAt });
            e.Property(x => x.IdempotencyKey).HasMaxLength(128).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
            e.Property(x => x.RequestHash).HasMaxLength(64).IsUnicode(false);
            e.Property(x => x.Currency).HasMaxLength(3).IsUnicode(false);
            e.Property(x => x.Kind).HasMaxLength(24);
            e.Property(x => x.Amount).HasPrecision(19, 2);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Wallet>().WithMany().HasForeignKey(x => x.SourceWalletId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Wallet>().WithMany().HasForeignKey(x => x.DestinationWalletId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable("Transfers", t => {
                t.HasCheckConstraint("CK_Transfer_Amount", "[Amount] > 0");
                t.HasCheckConstraint("CK_Transfer_DifferentWallets", "[SourceWalletId] <> [DestinationWalletId]");
                t.HasCheckConstraint("CK_Transfer_Currency", "[Currency] = 'BDT'");
            });
        });
        b.Entity<LedgerEntry>(e => {
            e.HasIndex(x => new { x.TransferId, x.WalletId }).IsUnique();
            e.HasIndex(x => new { x.WalletId, x.CreatedAt });
            e.Property(x => x.Debit).HasPrecision(19, 2);
            e.Property(x => x.Credit).HasPrecision(19, 2);
            e.Property(x => x.Currency).HasMaxLength(3).IsUnicode(false);
            e.HasOne(x => x.Transfer).WithMany().HasForeignKey(x => x.TransferId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Wallet).WithMany().HasForeignKey(x => x.WalletId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable("LedgerEntries", t => {
                t.HasCheckConstraint("CK_Ledger_OneSide", "([Debit] > 0 AND [Credit] = 0) OR ([Credit] > 0 AND [Debit] = 0)");
                t.HasCheckConstraint("CK_Ledger_Currency", "[Currency] = 'BDT'");
            });
        });
        b.Entity<AuthSession>().HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<AuthSession>().HasIndex(x => x.UserId);
        b.Entity<RefreshToken>(e => {
            e.HasIndex(x => x.Hash).IsUnique();
            e.Property(x => x.Hash).HasMaxLength(64).IsUnicode(false);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasOne(x => x.Session).WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<AccountChallenge>(e => {
            e.Property(x => x.Purpose).HasMaxLength(32);
            e.Property(x => x.TokenHash).HasMaxLength(64).IsUnicode(false);
            e.Property(x => x.SecurityStamp).HasMaxLength(128);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.UserId, x.Purpose, x.CreatedAt });
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<EmailOutbox>(e => {
            e.Property(x => x.LastErrorType).HasMaxLength(128);
            e.HasIndex(x => new { x.SentAt, x.FailedAt, x.NextAttemptAt });
        });
        b.Entity<AuthSession>().Property(x => x.SecurityStamp).HasMaxLength(128);
        b.Entity<AuditEvent>().Property(x => x.Reason).HasMaxLength(500);
        b.Entity<AuditEvent>().Property(x => x.Action).HasMaxLength(64);
    }

    private void ValidateFinancialWrites()
    {
        ChangeTracker.DetectChanges();
        foreach (var entry in ChangeTracker.Entries())
            if ((entry.Entity is Transfer or LedgerEntry or AuditEvent) &&
                entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Posted financial and audit records are append-only.");
        var transfers = ChangeTracker.Entries<Transfer>().Where(x => x.State == EntityState.Added).Select(x => x.Entity).ToList();
        var lines = ChangeTracker.Entries<LedgerEntry>().Where(x => x.State == EntityState.Added).Select(x => x.Entity).ToList();
        foreach (var transfer in transfers)
        {
            var pair = lines.Where(x => x.TransferId == transfer.Id).ToArray();
            if (pair.Length != 2 || pair.Any(x => x.Currency != transfer.Currency) ||
                pair.Count(x => x.WalletId == transfer.SourceWalletId && x.Debit == transfer.Amount && x.Credit == 0) != 1 ||
                pair.Count(x => x.WalletId == transfer.DestinationWalletId && x.Credit == transfer.Amount && x.Debit == 0) != 1)
                throw new InvalidOperationException("Every posting must have matching debit and credit entries.");
        }
        if (lines.Any(x => transfers.All(t => t.Id != x.TransferId)))
            throw new InvalidOperationException("Ledger entries must be created with their transfer.");
    }
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    { ValidateFinancialWrites(); return base.SaveChanges(acceptAllChangesOnSuccess); }
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    { ValidateFinancialWrites(); return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken); }
}
