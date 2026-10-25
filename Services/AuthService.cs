using System.Data;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FinTechWalletAPI.Contracts;
using FinTechWalletAPI.Data;
using FinTechWalletAPI.Infrastructure;
using FinTechWalletAPI.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
namespace FinTechWalletAPI.Services;

public sealed class AuthService(AppDbContext db, UserManager<User> users,
    SignInManager<User> signIn, IOptions<JwtSettings> options, AccountSecurityService accountSecurity)
{
    private readonly JwtSettings settings = options.Value;
    public async Task<object> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = new User { Id = Guid.NewGuid(), Email = request.Email.Trim(), UserName = request.Email.Trim() };
        var result = await users.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            if (result.Errors.Any(x => x.Code.StartsWith("Duplicate", StringComparison.Ordinal)))
                throw new ApiException(409, "account_exists", "An account with these details already exists.");
            throw new ApiException(400, "registration_invalid", string.Join(" ", result.Errors.Select(x => x.Description)));
        }
        var wallet = new Wallet { UserId = user.Id };
        db.Wallets.Add(wallet);
        accountSecurity.QueueChallenge(user, ChallengeTokens.VerifyEmail);
        db.AuditEvents.Add(new AuditEvent { ActorId = user.Id, TargetId = wallet.Id, Action = "Account.Registered" });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new { user.Id, user.Email, WalletId = wallet.Id, EmailVerificationRequired = true };
    }
    public async Task<TokenResponse> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is null || !(await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true)).Succeeded)
            {
            // Persist Identity's failed-login counter/lockout even though the request is rejected.
            await tx.CommitAsync(ct);
            throw new ApiException(401, "invalid_credentials", "Invalid credentials, unverified email or account temporarily unavailable.");
        }
        var session = new AuthSession { UserId = user.Id, SecurityStamp = user.SecurityStamp ?? "", ExpiresAt = DateTimeOffset.UtcNow.AddDays(settings.SessionDays) };
        db.Sessions.Add(session);
        var response = Issue(session);
        db.AuditEvents.Add(new AuditEvent { ActorId = user.Id, TargetId = session.Id, Action = "Session.Created" });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return response;
    }
    public async Task<TokenResponse> RefreshAsync(string raw, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var hash = Hash(raw);
        var token = await db.RefreshTokens.Include(x => x.Session).ThenInclude(x => x.User).SingleOrDefaultAsync(x => x.Hash == hash, ct);
        var now = DateTimeOffset.UtcNow;
        if (token is null || token.Session.RevokedAt is not null || token.Session.ExpiresAt <= now ||
            !token.Session.User.EmailConfirmed || token.Session.SecurityStamp != token.Session.User.SecurityStamp)
            throw new ApiException(401, "invalid_refresh_token", "Refresh token is invalid or expired.");
        if (token.UsedAt is not null)
        {
            token.Session.RevokedAt = now;
            db.AuditEvents.Add(new AuditEvent { ActorId = token.Session.UserId, TargetId = token.SessionId, Action = "Session.RefreshReplay" });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            throw new ApiException(401, "refresh_reuse", "Refresh token reuse detected. Sign in again.");
        }
        token.UsedAt = now;
        var response = Issue(token.Session);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return response;
    }
    public async Task LogoutAsync(Guid actor, Guid sessionId, CancellationToken ct)
    {
        var session = await db.Sessions.SingleAsync(x => x.Id == sessionId && x.UserId == actor, ct);
        session.RevokedAt = DateTimeOffset.UtcNow;
        db.AuditEvents.Add(new AuditEvent { ActorId = actor, TargetId = sessionId, Action = "Session.Revoked" });
        await db.SaveChangesAsync(ct);
    }
    private TokenResponse Issue(AuthSession session)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddMinutes(settings.AccessTokenMinutes);
        if (expires > session.ExpiresAt) expires = session.ExpiresAt;
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        db.RefreshTokens.Add(new RefreshToken { SessionId = session.Id, Hash = Hash(raw) });
        var jwt = new JwtSecurityToken(settings.Issuer, settings.Audience,
            [new Claim("sub", session.UserId.ToString()), new Claim("sid", session.Id.ToString()),
             new Claim("jti", Guid.NewGuid().ToString())], now.UtcDateTime, expires.UtcDateTime,
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SigningKey)), SecurityAlgorithms.HmacSha256));
        return new(new JwtSecurityTokenHandler().WriteToken(jwt), raw, expires, session.Id);
    }
    private static string Hash(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}
