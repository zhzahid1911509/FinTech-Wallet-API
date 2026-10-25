using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using FinTechWalletAPI.Contracts;
using FinTechWalletAPI.Data;
using FinTechWalletAPI.Infrastructure;
using FinTechWalletAPI.Models;
using FinTechWalletAPI.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
var connection = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:DefaultConnection using user-secrets or environment variables.");
var jwt = builder.Configuration.GetSection("Jwt").Get<JwtSettings>() ?? new JwtSettings();
if (Encoding.UTF8.GetByteCount(jwt.SigningKey) < 32 || string.IsNullOrWhiteSpace(jwt.Issuer) ||
    string.IsNullOrWhiteSpace(jwt.Audience) || jwt.AccessTokenMinutes is < 1 or > 30 || jwt.SessionDays is < 1 or > 30)
    throw new InvalidOperationException("Configure Jwt:SigningKey (at least 32 random bytes), issuer, audience and valid token lifetimes.");
builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection("Jwt"));
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlServer(connection));
builder.Services.AddIdentityCore<User>(o => {
    o.User.RequireUniqueEmail = true;
    o.Password.RequiredLength = 12;
    o.Lockout.MaxFailedAccessAttempts = 5;
    o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
}).AddEntityFrameworkStores<AppDbContext>().AddSignInManager();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o => {
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters {
        ValidateIssuer = true, ValidIssuer = jwt.Issuer,
        ValidateAudience = true, ValidAudience = jwt.Audience,
        ValidateLifetime = true, RequireExpirationTime = true,
        ValidateIssuerSigningKey = true, RequireSignedTokens = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256], ClockSkew = TimeSpan.FromSeconds(15), NameClaimType = "sub"
    };
    o.Events = new JwtBearerEvents {
        OnTokenValidated = async context => {
            if (!Guid.TryParse(context.Principal?.FindFirstValue("sub"), out var userId) ||
                !Guid.TryParse(context.Principal?.FindFirstValue("sid"), out var sessionId)) {
                context.Fail("Invalid session."); return;
            }
            var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
            var now = DateTimeOffset.UtcNow;
            if (!await db.Sessions.AsNoTracking().AnyAsync(s => s.Id == sessionId && s.UserId == userId &&
                s.RevokedAt == null && s.ExpiresAt > now, context.HttpContext.RequestAborted)) context.Fail("Session expired or revoked.");
        }
    };
});
builder.Services.AddAuthorization();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<TransferService>();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddRateLimiter(o => {
    o.RejectionStatusCode = 429;
    o.AddPolicy("auth", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("wallet", c => RateLimitPartition.GetFixedWindowLimiter(c.User.FindFirstValue("sub") ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
if (args.Contains("--seed-demo")) {
    using var scope = app.Services.CreateScope();
    await DemoSeeder.SeedAsync(scope.ServiceProvider, app.Configuration, app.Environment);
    return;
}
app.UseExceptionHandler();
app.UseStatusCodePages(async context => {
    await Results.Problem(statusCode: context.HttpContext.Response.StatusCode,
        extensions: new Dictionary<string, object?> { ["traceId"] = context.HttpContext.TraceIdentifier }).ExecuteAsync(context.HttpContext);
});
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
app.MapGet("/health/ready", async (AppDbContext db, CancellationToken ct) => {
    try { return await db.Database.CanConnectAsync(ct) && !((await db.Database.GetPendingMigrationsAsync(ct)).Any())
        ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503); }
    catch { return Results.StatusCode(503); }
});
var auth = app.MapGroup("/api/v1/auth").RequireRateLimiting("auth").AddEndpointFilter<ValidationFilter>();
auth.MapPost("/register", async (RegisterRequest r, AuthService service, CancellationToken ct) =>
    Results.Json(await service.RegisterAsync(r, ct), statusCode: 201));
auth.MapPost("/login", async (LoginRequest r, AuthService service, HttpContext context, CancellationToken ct) => {
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(await service.LoginAsync(r, ct));
});
auth.MapPost("/refresh", async (RefreshRequest r, AuthService service, HttpContext context, CancellationToken ct) => {
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(await service.RefreshAsync(r.RefreshToken, ct));
});
auth.MapPost("/logout", async (ClaimsPrincipal user, AuthService service, CancellationToken ct) => {
    await service.LogoutAsync(UserId(user), Guid.Parse(user.FindFirstValue("sid")!), ct);
    return Results.NoContent();
}).RequireAuthorization();
var api = app.MapGroup("/api/v1").RequireAuthorization().RequireRateLimiting("wallet");
api.AddEndpointFilter(async (context, next) => {
    context.HttpContext.Response.Headers.CacheControl = "no-store";
    return await next(context);
});
api.MapGet("/wallets/me", async (ClaimsPrincipal user, AppDbContext db, CancellationToken ct) => {
    var id = UserId(user);
    var wallet = await db.Wallets.AsNoTracking().Where(x => x.UserId == id)
        .Select(x => new WalletResponse(x.Id, x.Currency, x.Balance, x.IsFrozen)).SingleOrDefaultAsync(ct);
    return wallet is null ? Results.NotFound() : Results.Ok(wallet);
});
api.MapPost("/transfers", async (TransferRequest r, HttpContext context, TransferService service, CancellationToken ct) => {
    var result = await service.SendAsync(UserId(context.User), r, context.Request.Headers["Idempotency-Key"].ToString(), ct);
    context.Response.Headers["Idempotency-Replayed"] = result.Replayed ? "true" : "false";
    return result.Replayed ? Results.Ok(result.Transfer) : Results.Created($"/api/v1/transfers/{result.Transfer.Id}", result.Transfer);
});
api.MapGet("/transfers/{id:guid}", async (Guid id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) => {
    var actor = UserId(user);
    var walletIds = db.Wallets.Where(x => x.UserId == actor).Select(x => x.Id);
    var transfer = await db.Transfers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id &&
        (walletIds.Contains(x.SourceWalletId) || walletIds.Contains(x.DestinationWalletId)), ct);
    return transfer is null ? Results.NotFound() : Results.Ok(TransferService.ToResponse(transfer));
});
api.MapGet("/transactions", async (int? page, int? pageSize, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) => {
    var p = page ?? 1; var size = pageSize ?? 20;
    if (p < 1 || p > 100_000 || size < 1 || size > 100) throw new ApiException(400, "invalid_pagination", "Page must be 1–100000; pageSize must be 1–100.");
    var actor = UserId(user);
    var walletIds = db.Wallets.Where(x => x.UserId == actor).Select(x => x.Id);
    var query = db.Transfers.AsNoTracking().Where(x => walletIds.Contains(x.SourceWalletId) || walletIds.Contains(x.DestinationWalletId));
    var items = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Skip((p - 1) * size).Take(size + 1).ToListAsync(ct);
    return Results.Ok(new { page = p, pageSize = size, hasMore = items.Count > size, items = items.Take(size).Select(TransferService.ToResponse) });
});
app.Run();
static Guid UserId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue("sub")!);
public partial class Program { }
