using Microsoft.AspNetCore.Identity;
namespace FinTechWalletAPI.Models;
public sealed class User : IdentityUser<Guid>
{
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
