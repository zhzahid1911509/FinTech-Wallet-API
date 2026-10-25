using FinTechWalletAPI.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace FinTechWalletAPI.Tests;
public sealed class ModelConsistencyTests
{
    [Fact]
    public void DesignTimeModelMatchesSnapshot()
    {
        using var db = new DesignTimeDbContextFactory().CreateDbContext([]);
        Assert.False(db.Database.HasPendingModelChanges());
    }
    [Fact]
    public void RuntimeModelMatchesSnapshot()
    {
        using var factory = new Factory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(db.Database.HasPendingModelChanges());
    }
    private sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Email:WorkerEnabled", "false");
            builder.UseSetting("ConnectionStrings:DefaultConnection", "Server=localhost;Database=NeverConnect;Integrated Security=True");
            builder.UseSetting("Jwt:SigningKey", "model-tests-only-not-a-production-key-239!");
        }
    }
}
