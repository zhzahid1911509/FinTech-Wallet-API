using System.Data;
using FinTechWalletAPI.Contracts;
using FinTechWalletAPI.Data;
using FinTechWalletAPI.Infrastructure;
using FinTechWalletAPI.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
namespace FinTechWalletAPI.Services;
public sealed class TransferRiskService(AppDbContext db, MfaService mfa, TimeProvider clock, IOptions<TransferRiskSettings> options)
{
    private readonly TransferRiskSettings settings = options.Value;
    // Called inside the posting transaction as well as the advisory assessment transaction.
    public async Task<AuthSession> RequireSessionAsync(Guid actor, Guid sessionId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var session = await db.Sessions.Include(x => x.User).SingleOrDefaultAsync(x => x.Id == sessionId && x.UserId == actor, ct);
        if (session is null || session.RevokedAt is not null || session.ExpiresAt <= now || !session.User.EmailConfirmed || session.SecurityStamp != session.User.SecurityStamp)
            throw new ApiException(401, "invalid_session", "Sign in again.");
        return session;
    }
    public async Task<TransferRiskResponse> EvaluateAsync(AuthSession session, Guid sender, TransferRequest request, CancellationToken ct)
    {
        var since = clock.GetUtcNow().AddHours(-24);
        var outgoing = await db.Transfers.Where(x => x.SourceWalletId == sender && x.CreatedAt >= since && x.Kind == "Transfer")
            .SumAsync(x => (decimal?)x.Amount, ct) ?? 0m;
        var known = await db.Transfers.AnyAsync(x => x.SourceWalletId == sender && x.DestinationWalletId == request.RecipientWalletId && x.Kind == "Transfer", ct);
        var reasons = settings.Reasons(request.Amount, outgoing, known, session.AuthenticationMethod == "recovery");
        return new(reasons.Length != 0, reasons, outgoing, session.User.TwoFactorEnabled);
    }
    public async Task<TransferRiskResponse> AssessAsync(Guid actor, Guid sessionId, TransferRequest request, CancellationToken ct)
    {
        MoneyRules.Validate(request.Amount, "assessment");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var session = await RequireSessionAsync(actor, sessionId, ct);
        var sender = await ValidateWalletsAsync(actor, request.RecipientWalletId, ct);
        var result = await EvaluateAsync(session, sender, request, ct);
        await tx.CommitAsync(ct);
        return result;
    }
    public async Task<TransferAuthorizationResponse> AuthorizeAsync(Guid actor, Guid sessionId, TransferAuthorizationRequest request, string key, CancellationToken ct)
    {
        MoneyRules.Validate(request.Amount, key);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var user = await mfa.RequirePasswordAsync(actor, sessionId, request.CurrentPassword, tx, ct);
        if (!user.TwoFactorEnabled) throw new ApiException(403, "transfer_mfa_enrollment_required", "Enroll an authenticator and sign in again before authorizing this transfer.");
        await ValidateWalletsAsync(actor, request.RecipientWalletId, ct);
        var existing = await db.Transfers.AnyAsync(x => x.ActorId == actor && x.IdempotencyKey == key, ct);
        if (existing) throw new ApiException(409, "transfer_already_posted", "Retry POST /transfers with the original details and key to retrieve the posted result.");
        var error = await mfa.VerifyAsync(user, request.AuthenticatorCode, null, false, ct);
        if (error is not null)
        {
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            throw new ApiException(403, error, "A fresh authenticator code is required. Recovery codes cannot authorize a transfer.");
        }
        var now = clock.GetUtcNow();
        var authorization = new TransferAuthorization { SessionId = sessionId, RequestHash = MoneyRules.Fingerprint(request.RecipientWalletId, request.Amount),
            IdempotencyKey = key, CreatedAt = now, ExpiresAt = now.AddSeconds(settings.AuthorizationSeconds) };
        db.TransferAuthorizations.Add(authorization);
        db.AuditEvents.Add(new AuditEvent { ActorId = actor, TargetId = authorization.Id, Action = "Transfer.AuthorizationCreated" });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(authorization.Id, request.RecipientWalletId, request.Amount, "BDT", key, authorization.ExpiresAt);
    }
    // Consumption is persisted atomically with the transfer and its two ledger lines.
    public async Task<TransferAuthorization?> FindAuthorizationAsync(Guid? id, Guid sessionId, string key, string hash, CancellationToken ct)
    {
        if (id is null) return null;
        var item = await db.TransferAuthorizations.SingleOrDefaultAsync(x => x.Id == id && x.SessionId == sessionId, ct);
        var now = clock.GetUtcNow();
        return item is not null && item.RequestHash == hash && item.IdempotencyKey == key && item.ConsumedByTransferId is null &&
            item.CreatedAt <= now && item.ExpiresAt > now ? item : null;
    }
    private async Task<Guid> ValidateWalletsAsync(Guid actor, Guid recipient, CancellationToken ct)
    {
        var sender = await db.Wallets.SingleOrDefaultAsync(x => x.UserId == actor && !x.IsSystem, ct)
            ?? throw new ApiException(404, "wallet_not_found", "Your wallet was not found.");
        if (sender.Id == recipient) throw new ApiException(400, "self_transfer", "Cannot transfer to your own wallet.");
        var receiver = await db.Wallets.SingleOrDefaultAsync(x => x.Id == recipient && !x.IsSystem, ct)
            ?? throw new ApiException(404, "recipient_not_found", "Recipient wallet was not found.");
        if (sender.IsFrozen || receiver.IsFrozen) throw new ApiException(409, "wallet_frozen", "A wallet is frozen.");
        return sender.Id;
    }
}
