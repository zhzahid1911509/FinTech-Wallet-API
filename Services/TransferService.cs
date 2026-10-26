using System.Data;
using FinTechWalletAPI.Contracts;
using FinTechWalletAPI.Data;
using FinTechWalletAPI.Infrastructure;
using FinTechWalletAPI.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
namespace FinTechWalletAPI.Services;

public sealed class TransferService(AppDbContext db, TransferRiskService risk, TimeProvider clock)
{
    public async Task<TransferOutcome> SendAsync(Guid actor, Guid sessionId, TransferRequest request, string key, CancellationToken ct)
    {
        MoneyRules.Validate(request.Amount, key);
        if (request.RecipientWalletId == Guid.Empty)
            throw new ApiException(400, "invalid_recipient", "A recipient wallet ID is required.");
        var hash = MoneyRules.Fingerprint(request.RecipientWalletId, request.Amount);
        // Retry the whole transaction after a deadlock/concurrency loss. Never retry just one debit.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try { return await SendOnce(actor, sessionId, request, key, hash, ct); }
            catch (Exception e) when (DatabaseErrors.IsRetryableTransfer(e))
            {
                db.ChangeTracker.Clear();
                if (attempt == 3)
                    throw new ApiException(409, "transfer_conflict", "Transfer contention. Retry with the SAME Idempotency-Key.");
                await Task.Delay(Random.Shared.Next(20, 80) * (attempt + 1), ct);
            }
        }
        throw new InvalidOperationException("Unreachable retry state.");
    }
    private async Task<TransferOutcome> SendOnce(Guid actor, Guid sessionId, TransferRequest request, string key, string hash, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var session = await risk.RequireSessionAsync(actor, sessionId, ct);
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
        var assessment = await risk.EvaluateAsync(session, sender.Id, request, ct);
        var authorization = await risk.FindAuthorizationAsync(request.AuthorizationId, sessionId, key, hash, ct);
        if ((assessment.AuthorizationRequired || request.AuthorizationId.HasValue) && authorization is null)
        {
            var code = request.AuthorizationId.HasValue ? "transfer_authorization_invalid" :
                session.User.TwoFactorEnabled ? "transfer_authorization_required" : "transfer_mfa_enrollment_required";
            db.AuditEvents.Add(new AuditEvent { ActorId = actor, TargetId = sender.Id, Action = "Transfer.RiskDenied",
                Reason = code + ":" + string.Join(",", assessment.Reasons) });
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            throw new ApiException(403, code, "Assess this transfer at /api/v1/transfers/assess, then obtain a transaction-specific approval at /api/v1/transfers/authorizations using your password and a fresh authenticator code. Enroll MFA first if needed.");
        }
        var transfer = new Transfer { ActorId = actor, SourceWalletId = sender.Id,
            DestinationWalletId = receiver.Id, Amount = request.Amount, Currency = sender.Currency,
            IdempotencyKey = key, RequestHash = hash, CreatedAt = clock.GetUtcNow() };
        if (authorization is not null)
        {
            authorization.ConsumedByTransferId = transfer.Id;
            db.AuditEvents.Add(new AuditEvent { ActorId = actor, TargetId = transfer.Id, Action = "Transfer.AuthorizationConsumed",
                Reason = authorization.Id.ToString() });
        }
        db.AuditEvents.Add(new AuditEvent { ActorId = actor, TargetId = transfer.Id, Action = "Transfer.RiskAccepted",
            Reason = assessment.Reasons.Length == 0 ? "low_risk" : string.Join(",", assessment.Reasons) });
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
    public static TransferResponse ToResponse(Transfer t) => new(t.Id, t.SourceWalletId, t.DestinationWalletId, t.Amount, t.Currency, t.Kind, t.CreatedAt);
}
