using System.Data;
using FinTechWalletAPI.Contracts;
using FinTechWalletAPI.Data;
using FinTechWalletAPI.Infrastructure;
using FinTechWalletAPI.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
namespace FinTechWalletAPI.Services;

public sealed class TransferService(AppDbContext db)
{
    public async Task<TransferOutcome> SendAsync(Guid actor, TransferRequest request, string key, CancellationToken ct)
    {
        MoneyRules.Validate(request.Amount, key);
        if (request.RecipientWalletId == Guid.Empty)
            throw new ApiException(400, "invalid_recipient", "A recipient wallet ID is required.");
        var hash = MoneyRules.Fingerprint(request.RecipientWalletId, request.Amount);
        // Retry the whole transaction after a deadlock/concurrency loss. Never retry just one debit.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try { return await SendOnce(actor, request, key, hash, ct); }
            catch (Exception e) when (IsRetryable(e))
            {
                db.ChangeTracker.Clear();
                if (attempt == 3)
                    throw new ApiException(409, "transfer_conflict", "Transfer contention. Retry with the SAME Idempotency-Key.");
                await Task.Delay(Random.Shared.Next(20, 80) * (attempt + 1), ct);
            }
        }
        throw new InvalidOperationException("Unreachable retry state.");
    }
    private async Task<TransferOutcome> SendOnce(Guid actor, TransferRequest request, string key, string hash, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var existing = await db.Transfers.AsNoTracking().SingleOrDefaultAsync(x => x.ActorId == actor && x.IdempotencyKey == key, ct);
        if (existing is not null)
        {
            if (existing.RequestHash != hash) throw new ApiException(409, "idempotency_mismatch", "This key was already used with different transfer data.");
            await tx.CommitAsync(ct);
            return new(ToResponse(existing), true);
        }
        var sender = await db.Wallets.SingleOrDefaultAsync(x => x.UserId == actor && !x.IsSystem, ct)
            ?? throw new ApiException(404, "wallet_not_found", "Your wallet was not found.");
        if (sender.Id == request.RecipientWalletId) throw new ApiException(400, "self_transfer", "Cannot transfer to your own wallet.");
        var receiver = await db.Wallets.SingleOrDefaultAsync(x => x.Id == request.RecipientWalletId && !x.IsSystem, ct)
            ?? throw new ApiException(404, "recipient_not_found", "Recipient wallet was not found.");
        if (sender.IsFrozen || receiver.IsFrozen) throw new ApiException(409, "wallet_frozen", "A wallet is frozen.");
        if (sender.Currency != receiver.Currency) throw new ApiException(400, "currency_mismatch", "Wallet currencies must match.");
        if (sender.Balance < request.Amount) throw new ApiException(422, "insufficient_funds", "Insufficient available funds.");
        if (receiver.Balance > MoneyRules.MaximumBalance - request.Amount) throw new ApiException(409, "balance_limit", "Recipient balance limit exceeded.");
        var transfer = new Transfer { ActorId = actor, SourceWalletId = sender.Id,
            DestinationWalletId = receiver.Id, Amount = request.Amount, Currency = sender.Currency,
            IdempotencyKey = key, RequestHash = hash };
        sender.Balance -= request.Amount;
        receiver.Balance += request.Amount;
        AddPosting(db, transfer);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(ToResponse(transfer), false);
    }
    internal static void AddPosting(AppDbContext db, Transfer t)
    {
        db.Transfers.Add(t);
        db.LedgerEntries.AddRange(
            new LedgerEntry { TransferId = t.Id, WalletId = t.SourceWalletId, Debit = t.Amount, Currency = t.Currency, CreatedAt = t.CreatedAt },
            new LedgerEntry { TransferId = t.Id, WalletId = t.DestinationWalletId, Credit = t.Amount, Currency = t.Currency, CreatedAt = t.CreatedAt });
        db.AuditEvents.Add(new AuditEvent { ActorId = t.ActorId, TargetId = t.Id, Action = t.Kind + ".Posted" });
    }
    private static bool IsRetryable(Exception e) => e is DbUpdateConcurrencyException ||
        e is SqlException { Number: 1205 or 2601 or 2627 } ||
        e is DbUpdateException { InnerException: SqlException { Number: 1205 or 2601 or 2627 } };
    public static TransferResponse ToResponse(Transfer t) => new(t.Id, t.SourceWalletId, t.DestinationWalletId, t.Amount, t.Currency, t.Kind, t.CreatedAt);
}
