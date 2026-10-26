using System.Data;
using FinTechWalletAPI.Contracts;
using FinTechWalletAPI.Data;
using FinTechWalletAPI.Infrastructure;
using FinTechWalletAPI.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
namespace FinTechWalletAPI.Services;
public sealed class WalletApprovalService(AppDbContext db, UserManager<User> users, MfaService mfa, TimeProvider clock)
{
    public async Task<WalletApprovalResponse> RequestAsync(Guid actor, Guid sessionId, Guid walletId, ApprovalActionRequest request, CancellationToken ct)
    {
        ValidateReason(request.Reason);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var user = await mfa.RequirePasswordAsync(actor, sessionId, request.CurrentPassword, tx, ct);
        await RequireOperatorAsync(user, ct); await mfa.RequireRecentAsync(actor, sessionId, ct);
        var wallet = await db.Wallets.SingleOrDefaultAsync(x => x.Id == walletId && !x.IsSystem, ct)
            ?? throw new ApiException(404, "wallet_not_found", "Customer wallet was not found.");
        if (!wallet.IsFrozen) throw new ApiException(409, "wallet_not_frozen", "Wallet is not frozen.");
        var now = clock.GetUtcNow();
        var existing = await db.WalletApprovals.SingleOrDefaultAsync(x => x.WalletId == walletId && x.Status == "Pending", ct);
        if (existing is not null) {
            if (existing.ExpiresAt > now) throw new ApiException(409, "approval_pending", "A pending unfreeze request already exists for this wallet.");
            existing.Status = "Expired";
            Audit(actor, existing.Id, "Wallet.UnfreezeExpired", "Approval window expired.");
            await db.SaveChangesAsync(ct);
        }
        var item = new WalletApproval { WalletId = walletId, WalletStateVersion = wallet.StateVersion,
            RequestedBy = actor, RequesterSecurityStamp = user.SecurityStamp!, RequestedAt = now,
            ExpiresAt = now.AddMinutes(30), Reason = request.Reason.Trim() };
        db.WalletApprovals.Add(item); Audit(actor, item.Id, "Wallet.UnfreezeRequested", item.Reason);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return ToResponse(item);
    }
    public async Task<WalletApprovalResponse> DecideAsync(Guid actor, Guid sessionId, Guid id, string decision, ApprovalActionRequest request, CancellationToken ct)
    {
        ValidateReason(request.Reason);
        if (decision is not ("approve" or "reject" or "cancel")) throw new ApiException(400, "invalid_decision", "Unsupported decision.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var user = await mfa.RequirePasswordAsync(actor, sessionId, request.CurrentPassword, tx, ct);
        await RequireOperatorAsync(user, ct); await mfa.RequireRecentAsync(actor, sessionId, ct);
        var item = await db.WalletApprovals.SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new ApiException(404, "approval_not_found", "Unfreeze request was not found.");
        if (item.Status != "Pending") throw new ApiException(409, "approval_terminal", "This request has already been decided.");
        if (decision == "cancel" && actor != item.RequestedBy) throw new ApiException(403, "requester_required", "Only the requester can cancel this request.");
        if (decision != "cancel" && actor == item.RequestedBy) throw new ApiException(403, "different_reviewer_required", "A different operator must review this request.");
        var now = clock.GetUtcNow();
        if (item.ExpiresAt <= now) {
            item.Status = "Expired"; Audit(actor, id, "Wallet.UnfreezeExpired", "Approval window expired.");
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            throw new ApiException(409, "approval_expired", "The approval window expired; create a new request.");
        }
        if (decision == "approve")
        {
            var requester = await users.FindByIdAsync(item.RequestedBy.ToString());
            var wallet = await db.Wallets.SingleAsync(x => x.Id == item.WalletId, ct);
            if (requester is null || !requester.EmailConfirmed || !requester.TwoFactorEnabled ||
                requester.SecurityStamp != item.RequesterSecurityStamp || !await users.IsInRoleAsync(requester, "WalletOperator") ||
                !wallet.IsFrozen || wallet.StateVersion != item.WalletStateVersion || wallet.IsSystem)
            {
                item.Status = "Invalidated"; Audit(actor, id, "Wallet.UnfreezeInvalidated", "Requester security or wallet state changed.");
                await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
                throw new ApiException(409, "approval_invalidated", "Requester security or wallet state changed; create a new request.");
            }
            wallet.IsFrozen = false; wallet.StateVersion++;
            item.Status = "Approved";
            Audit(actor, wallet.Id, "Wallet.Unfrozen", item.Reason);
        }
        else item.Status = decision == "cancel" ? "Cancelled" : "Rejected";
        item.ReviewedBy = actor; item.ReviewedAt = now; item.ReviewReason = request.Reason.Trim();
        Audit(actor, id, "Wallet.Unfreeze" + item.Status, item.ReviewReason);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return ToResponse(item);
    }
    private async Task RequireOperatorAsync(User user, CancellationToken ct)
    {
        if (!await users.IsInRoleAsync(user, "WalletOperator")) throw new ApiException(403, "operator_required", "Operator permission is required.");
    }
    private static void ValidateReason(string reason)
    { if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5 || reason.Length > 500) throw new ApiException(400, "reason_required", "Provide a reason of 5–500 characters."); }
    private void Audit(Guid actor, Guid target, string action, string reason) => db.AuditEvents.Add(new AuditEvent { ActorId = actor, TargetId = target, Action = action, Reason = reason });
    public static WalletApprovalResponse ToResponse(WalletApproval x) => new(x.Id, x.WalletId, x.RequestedBy, x.Reason, x.Status, x.RequestedAt, x.ExpiresAt, x.ReviewedBy, x.ReviewedAt, x.ReviewReason);
}
