using System.Data;
using FinTechWalletAPI.Contracts;
using FinTechWalletAPI.Data;
using FinTechWalletAPI.Infrastructure;
using FinTechWalletAPI.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
namespace FinTechWalletAPI.Services;
public sealed class MfaService(AppDbContext db, UserManager<User> users, SignInManager<User> signIn,
    MfaSecrets secrets, EmailEnvelope envelope, TimeProvider clock)
{
    // Caller must own a serializable transaction and commit failures to preserve MFA lockout counters.
    public async Task<string?> VerifyAsync(User user, string? code, string? recovery, bool allowRecovery, CancellationToken ct)
    {
        var profile = await db.MfaProfiles.SingleOrDefaultAsync(x => x.UserId == user.Id, ct);
        if (profile is null) return "mfa_not_enrolled";
        var now = clock.GetUtcNow();
        if (profile.LockedUntil > now) return "mfa_locked";
        if (profile.EnrollmentExpiresAt is not null && profile.EnrollmentExpiresAt <= now) return "mfa_enrollment_expired";
        if (string.IsNullOrEmpty(code) && string.IsNullOrEmpty(recovery)) return "mfa_required";
        var valid = false;
        if (!string.IsNullOrEmpty(code) && string.IsNullOrEmpty(recovery))
        {
            valid = MfaSecrets.Verify(secrets.Unprotect(profile.ProtectedSecret), code, now, profile.LastAcceptedStep, out var step);
            if (valid) profile.LastAcceptedStep = step;
        }
        else if (allowRecovery && string.IsNullOrEmpty(code) && !string.IsNullOrEmpty(recovery))
        {
            var hash = MfaSecrets.RecoveryHash(recovery);
            var stored = await db.MfaRecoveryCodes.SingleOrDefaultAsync(x => x.UserId == user.Id && x.Hash == hash && x.UsedAt == null, ct);
            valid = stored is not null;
            if (stored is not null) stored.UsedAt = now;
        }
        if (!valid)
        {
            profile.FailedAttempts++;
            if (profile.FailedAttempts >= 5) { profile.LockedUntil = now.AddMinutes(15); profile.FailedAttempts = 0; }
            db.AuditEvents.Add(new AuditEvent { ActorId = user.Id, TargetId = user.Id, Action = "Mfa.VerificationFailed" });
            return "mfa_invalid";
        }
        profile.FailedAttempts = 0; profile.LockedUntil = null;
        return null;
    }
    public async Task<User> RequirePasswordAsync(Guid actor, Guid sessionId, string password, IDbContextTransaction tx, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(actor.ToString());
        if (user is null || !(await signIn.CheckPasswordSignInAsync(user, password, true)).Succeeded)
        {
            await tx.CommitAsync(ct);
            throw new ApiException(403, "step_up_failed", "Current-password verification failed.");
        }
        var now = DateTimeOffset.UtcNow;
        if (!await db.Sessions.AnyAsync(x => x.Id == sessionId && x.UserId == actor && x.RevokedAt == null &&
            x.ExpiresAt > now && x.SecurityStamp == user.SecurityStamp, ct))
            throw new ApiException(401, "invalid_session", "Sign in again.");
        return user;
    }
    public async Task RequireRecentAsync(Guid actor, Guid sessionId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (!await db.Sessions.AnyAsync(x => x.Id == sessionId && x.UserId == actor && x.RevokedAt == null &&
            x.ExpiresAt > DateTimeOffset.UtcNow && x.User.TwoFactorEnabled && x.SecurityStamp == x.User.SecurityStamp &&
            x.AuthenticationMethod == "totp" && x.MfaVerifiedAt > now.AddMinutes(-5) && x.MfaVerifiedAt <= now, ct))
            throw new ApiException(403, "mfa_step_up_required", "Verify a fresh authenticator code with /api/v1/auth/mfa/step-up before this operation.");
    }
    public async Task<MfaEnrollmentResponse> BeginAsync(Guid actor, Guid sessionId, string password, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var user = await RequirePasswordAsync(actor, sessionId, password, tx, ct);
        if (user.TwoFactorEnabled) throw new ApiException(409, "mfa_already_enabled", "Disable the existing authenticator with proof before replacing it.");
        var profile = await db.MfaProfiles.SingleOrDefaultAsync(x => x.UserId == actor, ct);
        var key = secrets.Create(); var expiry = clock.GetUtcNow().AddMinutes(10);
        if (profile is null) { profile = new MfaProfile { UserId = actor }; db.MfaProfiles.Add(profile); }
        profile.ProtectedSecret = secrets.Protect(key); profile.EnrollmentExpiresAt = expiry;
        profile.LastAcceptedStep = -1; profile.FailedAttempts = 0; profile.LockedUntil = null;
        db.AuditEvents.Add(new AuditEvent { ActorId = actor, TargetId = actor, Action = "Mfa.EnrollmentStarted" });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(key, MfaSecrets.AuthenticatorUri(user.Email!, key), expiry);
    }
    public async Task<MfaRecoveryResponse> ConfirmAsync(Guid actor, Guid sessionId, string code, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var user = await users.FindByIdAsync(actor.ToString()) ?? throw new ApiException(401, "invalid_session", "Sign in again.");
        if (!await db.Sessions.AnyAsync(x => x.Id == sessionId && x.UserId == actor && x.RevokedAt == null && x.ExpiresAt > DateTimeOffset.UtcNow && x.SecurityStamp == user.SecurityStamp, ct))
            throw new ApiException(401, "invalid_session", "Sign in again.");
        if (user.TwoFactorEnabled) throw new ApiException(409, "mfa_already_enabled", "MFA is already enabled.");
        await RequireProofAsync(user, code, null, false, tx, ct);
        var profile = await db.MfaProfiles.SingleAsync(x => x.UserId == actor, ct);
        profile.EnrollmentExpiresAt = null;
        Check(await users.SetTwoFactorEnabledAsync(user, true));
        var codes = await ReplaceRecoveryCodesAsync(actor, ct);
        await RevokeSessionsAsync(user, ct);
        Notify(user, "MFA enabled", "An authenticator was enabled for your wallet. All existing sessions have been signed out.");
        db.AuditEvents.Add(new AuditEvent { ActorId = actor, TargetId = actor, Action = "Mfa.Enabled" });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(codes);
    }
    public async Task StepUpAsync(Guid actor, Guid sessionId, MfaProofRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var user = await RequirePasswordAsync(actor, sessionId, request.CurrentPassword, tx, ct);
        if (!user.TwoFactorEnabled) throw new ApiException(403, "mfa_not_enabled", "Enroll an authenticator first.");
        await RequireProofAsync(user, request.AuthenticatorCode, request.RecoveryCode, false, tx, ct);
        var session = await db.Sessions.SingleAsync(x => x.Id == sessionId && x.UserId == actor, ct);
        session.MfaVerifiedAt = clock.GetUtcNow(); session.AuthenticationMethod = "totp";
        db.AuditEvents.Add(new AuditEvent { ActorId = actor, TargetId = sessionId, Action = "Mfa.StepUp" });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    public async Task DisableAsync(Guid actor, Guid sessionId, MfaProofRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var user = await RequirePasswordAsync(actor, sessionId, request.CurrentPassword, tx, ct);
        if (!user.TwoFactorEnabled) throw new ApiException(409, "mfa_not_enabled", "MFA is not enabled.");
        await RequireProofAsync(user, request.AuthenticatorCode, request.RecoveryCode, true, tx, ct);
        // Save consumed proof before deleting its profile; transaction remains atomic.
        await db.SaveChangesAsync(ct);
        await db.MfaRecoveryCodes.Where(x => x.UserId == actor).ExecuteDeleteAsync(ct);
        db.MfaProfiles.Remove(await db.MfaProfiles.SingleAsync(x => x.UserId == actor, ct));
        Check(await users.SetTwoFactorEnabledAsync(user, false));
        await RevokeSessionsAsync(user, ct);
        Notify(user, "MFA disabled", "The authenticator was disabled for your wallet. All existing sessions have been signed out. Contact support if this was unexpected.");
        db.AuditEvents.Add(new AuditEvent { ActorId = actor, TargetId = actor, Action = "Mfa.Disabled" });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    public async Task<MfaRecoveryResponse> RegenerateAsync(Guid actor, Guid sessionId, MfaProofRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var user = await RequirePasswordAsync(actor, sessionId, request.CurrentPassword, tx, ct);
        if (!user.TwoFactorEnabled) throw new ApiException(409, "mfa_not_enabled", "MFA is not enabled.");
        await RequireProofAsync(user, request.AuthenticatorCode, request.RecoveryCode, false, tx, ct);
        var codes = await ReplaceRecoveryCodesAsync(actor, ct);
        await RevokeSessionsAsync(user, ct);
        Notify(user, "Recovery codes replaced", "New MFA recovery codes were generated. All previous recovery codes and sessions were invalidated.");
        db.AuditEvents.Add(new AuditEvent { ActorId = actor, TargetId = actor, Action = "Mfa.RecoveryCodesReplaced" });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(codes);
    }
    private async Task RequireProofAsync(User user, string? code, string? recovery, bool allowRecovery, IDbContextTransaction tx, CancellationToken ct)
    {
        var error = await VerifyAsync(user, code, recovery, allowRecovery, ct);
        if (error is null) return;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        throw new ApiException(403, error, "MFA proof was rejected. Codes are single-use; wait for a new authenticator code or check lockout status.");
    }
    private async Task<string[]> ReplaceRecoveryCodesAsync(Guid actor, CancellationToken ct)
    {
        await db.MfaRecoveryCodes.Where(x => x.UserId == actor).ExecuteDeleteAsync(ct);
        var codes = Enumerable.Range(0, 10).Select(_ => MfaSecrets.CreateRecoveryCode()).ToArray();
        db.MfaRecoveryCodes.AddRange(codes.Select(code => new MfaRecoveryCode { UserId = actor, Hash = MfaSecrets.RecoveryHash(code) }));
        return codes;
    }
    private async Task RevokeSessionsAsync(User user, CancellationToken ct)
    {
        Check(await users.UpdateSecurityStampAsync(user));
        await db.Sessions.Where(x => x.UserId == user.Id && x.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow), ct);
    }
    private void Notify(User user, string subject, string text) => db.EmailOutbox.Add(new EmailOutbox {
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(1), ProtectedPayload = envelope.Protect(new EmailPayload(user.Email!, subject, text)) });
    private static void Check(IdentityResult result)
    { if (!result.Succeeded) throw new ApiException(409, "account_update_conflict", "Account changed; retry the operation."); }
}
