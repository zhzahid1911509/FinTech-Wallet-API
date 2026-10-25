using FinTechWalletAPI.Data;
using FinTechWalletAPI.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
namespace FinTechWalletAPI.Services;
public static class DemoSeeder
{
    public static async Task SeedAsync(IServiceProvider services, IConfiguration config, IHostEnvironment environment)
    {
        if (!environment.IsDevelopment()) throw new InvalidOperationException("Demo seeding is Development-only.");
        var password = config["Demo:Password"];
        if (string.IsNullOrEmpty(password) || password.Length < 12)
            throw new InvalidOperationException("Set Demo:Password to a development-only password of at least 12 characters.");
        var db = services.GetRequiredService<AppDbContext>();
        var users = services.GetRequiredService<UserManager<User>>();
        // Deliberately refuses existing databases, preventing repeated funding or funding a real account.
        if (await db.Users.AnyAsync() || await db.Wallets.AnyAsync())
            throw new InvalidOperationException("Demo seeding requires an empty, migrated database.");
        await using var tx = await db.Database.BeginTransactionAsync();
        var treasury = new Wallet { IsSystem = true, Balance = -20_000m };
        db.Wallets.Add(treasury);
        foreach (var email in new[] { "alice@example.test", "bob@example.test" })
        {
            var user = new User { Id = Guid.NewGuid(), Email = email, UserName = email, EmailConfirmed = true };
            var result = await users.CreateAsync(user, password);
            if (!result.Succeeded) throw new InvalidOperationException(string.Join(" ", result.Errors.Select(x => x.Description)));
            var wallet = new Wallet { UserId = user.Id, Balance = 10_000m };
            db.Wallets.Add(wallet);
            TransferService.AddPosting(db, new Transfer { ActorId = user.Id, SourceWalletId = treasury.Id,
                DestinationWalletId = wallet.Id, Amount = 10_000m, Kind = "DemoFunding",
                IdempotencyKey = "demo-initial-funding", RequestHash = new string('0', 64) });
            await db.SaveChangesAsync();
        }
        await tx.CommitAsync();
        Console.WriteLine("Demo users created: alice@example.test and bob@example.test; each has 10,000 BDT of test funds.");
    }
}
