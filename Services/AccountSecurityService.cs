using System.Data;
using FinTechWalletAPI.Contracts;
using FinTechWalletAPI.Data;
using FinTechWalletAPI.Infrastructure;
using FinTechWalletAPI.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
namespace FinTechWalletAPI.Services;
public sealed class AccountSecurityService(AppDbContext db, UserManager<User> users, EmailEnvelope envelope)
{
    public const string GenericMessage = "If the account is eligible, an email will be sent shortly.";
    // Caller owns the transaction for registration. Other calls use RequestAsync.
    public void QueueChallenge(User user, string purpose)
    {
        var now = DateTimeOffset.UtcNow;
        var raw = ChallengeTokens.Create();
        var expiry = now.AddMinutes(purpose == ChallengeTokens.VerifyEmail ? 60 : 15);
        db.AccountChallenges.Add(new AccountChallenge { UserId = user.Id, Purpose = purpose,
            TokenHash = ChallengeTokens.Hash(raw), SecurityStamp = user.SecurityStamp ?? "", ExpiresAt = expiry });
        var endpoint = purpose == ChallengeTokens.VerifyEmail ? "/api/v1/auth/confirm-email" : "/api/v1/auth/reset-password";
        var text = $"FinTech Wallet account request\n\nEmail: {user.Email}\nToken: {raw}\nExpires (UTC): {expiry:O}\n\nSubmit this token to {endpoint} using your wallet client. " +
            "Do not share this token. If you did not request this, ignore this email.";
        db.EmailOutbox.Add(new EmailOutbox { ExpiresAt = expiry,
            ProtectedPayload = envelope.Protect(new EmailPayload(user.Email!, purpose == ChallengeTokens.VerifyEmail ? "Verify your wallet email" : "Reset your wallet password", text)) });
    }
    public async Task RequestAsync(string email, string purpose, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is null || (purpose == ChallengeTokens.VerifyEmail && user.EmailConfirmed) ||
            (purpose == ChallengeTokens.ResetPassword && !user.EmailConfirmed)) return;
        var now = DateTimeOffset.UtcNow;
        if (await db.AccountChallenges.AnyAsync(x => x.UserId == user.Id && x.Purpose == purpose && x.CreatedAt > now.AddMinutes(-1), ct)) return;
        // Invalidate older requests; only the newest emailed code remains valid.
        await db.AccountChallenges.Where(x => x.UserId == user.Id && x.Purpose == purpose && x.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedAt, now), ct);
        QueueChallenge(user, purpose);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
    public async Task ConfirmAsync(ConfirmEmailRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var (user, token) = await ResolveAsync(request.Email, request.Token, ChallengeTokens.VerifyEmail, ct);
        if (user.EmailConfirmed) throw InvalidToken();
        var result = await users.ConfirmEmailAsync(user, await users.GenerateEmailConfirmationTokenAsync(user));
        EnsureSuccess(result);
        token.UsedAt = DateTimeOffset.UtcNow;
        db.AuditEvents.Add(new AuditEvent { ActorId = user.Id, TargetId = user.Id, Action = "Account.EmailVerified" });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    public async Task ResetAsync(ResetPasswordRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var (user, token) = await ResolveAsync(request.Email, request.Token, ChallengeTokens.ResetPassword, ct);
        if (!user.EmailConfirmed) throw InvalidToken();
        var result = await users.ResetPasswordAsync(user, await users.GeneratePasswordResetTokenAsync(user), request.NewPassword);
        EnsureSuccess(result);
        token.UsedAt = DateTimeOffset.UtcNow;
        await db.Sessions.Where(x => x.UserId == user.Id && x.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow), ct);
        db.AuditEvents.Add(new AuditEvent { ActorId = user.Id, TargetId = user.Id, Action = "Account.PasswordReset" });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    public async Task RevokeAllAsync(Guid actor, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var user = await users.FindByIdAsync(actor.ToString()) ?? throw new ApiException(401, "invalid_session", "Session is invalid.");
        EnsureSuccess(await users.UpdateSecurityStampAsync(user));
        await db.Sessions.Where(x => x.UserId == actor && x.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow), ct);
        db.AuditEvents.Add(new AuditEvent { ActorId = actor, TargetId = actor, Action = "Session.RevokedAll" });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    public async Task RevokeAsync(Guid actor, Guid sessionId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var changed = await db.Sessions.Where(x => x.Id == sessionId && x.UserId == actor)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow), ct);
        if (changed == 0) throw new ApiException(404, "session_not_found", "Session was not found.");
        db.AuditEvents.Add(new AuditEvent { ActorId = actor, TargetId = sessionId, Action = "Session.Revoked" });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    private async Task<(User, AccountChallenge)> ResolveAsync(string email, string raw, string purpose, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is null) throw InvalidToken();
        var hash = ChallengeTokens.Hash(raw);
        var token = await db.AccountChallenges.SingleOrDefaultAsync(x => x.TokenHash == hash && x.UserId == user.Id, ct);
        if (token is null || !ChallengeTokens.IsUsable(token, purpose, user.SecurityStamp ?? "", DateTimeOffset.UtcNow)) throw InvalidToken();
        return (user, token);
    }
    private static ApiException InvalidToken() => new(400, "invalid_account_token", "The token is invalid, expired or already used.");
    private static void EnsureSuccess(IdentityResult result)
    {
        if (!result.Succeeded) throw new ApiException(400, "account_update_failed", string.Join(" ", result.Errors.Select(x => x.Description)));
    }
}
