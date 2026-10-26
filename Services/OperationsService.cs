using System.Data;
using System.Security.Claims;
using FinTechWalletAPI.Contracts;
using FinTechWalletAPI.Data;
using FinTechWalletAPI.Infrastructure;
using FinTechWalletAPI.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
namespace FinTechWalletAPI.Services;
public sealed class WalletOperatorRequirement : IAuthorizationRequirement { }
public sealed class WalletOperatorHandler(AppDbContext db) : AuthorizationHandler<WalletOperatorRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, WalletOperatorRequirement requirement)
    {
        if (!Guid.TryParse(context.User.FindFirstValue("sub"), out var id)) return;
        if (await (from membership in db.UserRoles join role in db.Roles on membership.RoleId equals role.Id
                   where membership.UserId == id && role.NormalizedName == "WALLETOPERATOR" select membership).AnyAsync())
            context.Succeed(requirement);
    }
}
public sealed class OperationsService(AppDbContext db, UserManager<User> users, MfaService mfa)
{
    public async Task<WalletResponse> SetWalletStateAsync(Guid actor, Guid sessionId, Guid walletId, WalletStateRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length < 5)
            throw new ApiException(400, "reason_required", "Provide an audit reason of at least five characters.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var user = await mfa.RequirePasswordAsync(actor, sessionId, request.CurrentPassword, tx, ct);
        await mfa.RequireRecentAsync(actor, sessionId, ct);
        if (!request.IsFrozen) throw new ApiException(409, "two_person_approval_required", "Create an unfreeze request for approval by a different operator.");
        if (!await users.IsInRoleAsync(user, "WalletOperator")) throw new ApiException(403, "operator_required", "Operator permission is required.");
        var wallet = await db.Wallets.SingleOrDefaultAsync(x => x.Id == walletId && !x.IsSystem, ct)
            ?? throw new ApiException(404, "wallet_not_found", "Customer wallet was not found.");
        if (wallet.IsFrozen != request.IsFrozen)
        {
            wallet.IsFrozen = request.IsFrozen;
            wallet.StateVersion++;
            db.AuditEvents.Add(new AuditEvent { ActorId = actor, TargetId = wallet.Id,
                Action = request.IsFrozen ? "Wallet.Frozen" : "Wallet.Unfrozen", Reason = request.Reason.Trim() });
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return new(wallet.Id, wallet.Currency, wallet.Balance, wallet.IsFrozen);
    }
    public static async Task SetOperatorAsync(IServiceProvider services, string email, bool grant)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var users = services.GetRequiredService<UserManager<User>>();
        var roles = services.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var user = await users.FindByEmailAsync(email) ?? throw new InvalidOperationException("Register the operator account first.");
        if (grant && !user.EmailConfirmed) throw new InvalidOperationException("Verify the operator email first.");
        if (grant && !await roles.RoleExistsAsync("WalletOperator")) Check(await roles.CreateAsync(new IdentityRole<Guid>("WalletOperator")));
        var hasRole = await users.IsInRoleAsync(user, "WalletOperator");
        if (grant != hasRole)
        {
            Check(grant ? await users.AddToRoleAsync(user, "WalletOperator") : await users.RemoveFromRoleAsync(user, "WalletOperator"));
            db.AuditEvents.Add(new AuditEvent { ActorId = user.Id, TargetId = user.Id,
                Action = grant ? "Operator.GrantedByConsole" : "Operator.RevokedByConsole", Reason = "Trusted server-console operation; ActorId identifies the affected account." });
            await db.SaveChangesAsync();
        }
        await tx.CommitAsync();
        Console.WriteLine(grant ? "Operator permission granted." : "Operator permission removed.");
    }
    private static void Check(IdentityResult result)
    { if (!result.Succeeded) throw new InvalidOperationException(string.Join(" ", result.Errors.Select(x => x.Description))); }
}
