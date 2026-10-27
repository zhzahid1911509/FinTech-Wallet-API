using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authorization;
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
builder.Services.AddOptions<EmailSettings>().Bind(builder.Configuration.GetSection("Email"))
    .Validate(x => x.Mode is "Pickup" or "Smtp", "Email:Mode must be Pickup or Smtp.")
    .Validate(x => x.MaxAttempts is >= 1 and <= 10 && x.Port is >= 1 and <= 65535, "Invalid email retry count or port.")
    .Validate(x => x.Tls is "StartTls" or "SslOnConnect", "SMTP requires StartTls or SslOnConnect.")
    .Validate(x => x.Mode != "Smtp" || !string.IsNullOrWhiteSpace(x.Host), "Set Email:Host for SMTP.")
    .Validate(x => MimeKit.MailboxAddress.TryParse(x.From, out _), "Set a valid Email:From address.")
    .Validate(x => builder.Environment.IsDevelopment() || (x.Mode == "Smtp" && x.WorkerEnabled), "Production requires SMTP delivery and an enabled worker.")
    .ValidateOnStart();
var protection = builder.Services.AddDataProtection().SetApplicationName("FinTechWalletAPI");
var keyDirectory = builder.Configuration["DataProtection:KeyDirectory"];
if (!builder.Environment.IsDevelopment())
{
    if (string.IsNullOrWhiteSpace(keyDirectory) || string.IsNullOrWhiteSpace(builder.Configuration["DataProtection:CertificatePath"]))
        throw new InvalidOperationException("Production requires DataProtection:KeyDirectory and CertificatePath to persist and encrypt the email key ring.");
    protection.ProtectKeysWithCertificate(X509CertificateLoader.LoadPkcs12FromFile(
        builder.Configuration["DataProtection:CertificatePath"]!, builder.Configuration["DataProtection:CertificatePassword"], X509KeyStorageFlags.EphemeralKeySet));
}
if (!string.IsNullOrWhiteSpace(keyDirectory)) protection.PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
builder.Services.AddSingleton<EmailEnvelope>();
builder.Services.AddSingleton<IEmailTransport, EmailTransport>();
builder.Services.AddScoped<EmailDispatcher>();
builder.Services.AddHostedService<EmailWorker>();
builder.Services.AddScoped<AccountSecurityService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<MfaSecrets>();
builder.Services.AddScoped<MfaService>();
builder.Services.AddScoped<WalletApprovalService>();
builder.Services.AddScoped<OperationsService>();
builder.Services.AddScoped<ReconciliationService>();
builder.Services.AddScoped<IReconciliationCheck>(sp => sp.GetRequiredService<ReconciliationService>());
builder.Services.AddOptions<ReconciliationSettings>().Bind(builder.Configuration.GetSection("Reconciliation"))
    .Validate(x => x.IsValid(), "Invalid Reconciliation interval, timeout, staleness or email backlog settings.").ValidateOnStart();
builder.Services.AddScoped<ReconciliationScheduler>();
builder.Services.AddScoped<OperationsMonitoring>();
builder.Services.AddHostedService<ReconciliationWorker>();
builder.Services.AddScoped<IAuthorizationHandler, WalletOperatorHandler>();
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlServer(connection));
builder.Services.AddIdentityCore<User>(o => {
    o.User.RequireUniqueEmail = true;
    o.SignIn.RequireConfirmedEmail = true;
    o.Password.RequiredLength = 12;
    o.Lockout.MaxFailedAccessAttempts = 5;
    o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
}).AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<AppDbContext>().AddSignInManager().AddDefaultTokenProviders();
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
                s.RevokedAt == null && s.ExpiresAt > now && s.User.EmailConfirmed && s.SecurityStamp == s.User.SecurityStamp, context.HttpContext.RequestAborted)) context.Fail("Session expired or revoked.");
        }
    };
});
builder.Services.AddAuthorization(o => o.AddPolicy("WalletOperator", p => p.RequireAuthenticatedUser().AddRequirements(new WalletOperatorRequirement())));
builder.Services.AddScoped<AuthService>();
builder.Services.AddOptions<TransferRiskSettings>().Bind(builder.Configuration.GetSection("TransferRisk"))
    .Validate(x => x.IsValid(), "TransferRisk thresholds must be positive BDT amounts up to 1,000,000 with two decimal places; AuthorizationSeconds must be 30–300.")
    .ValidateOnStart();
builder.Services.AddScoped<TransferRiskService>();
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
if (args.Contains("--grant-operator") || args.Contains("--revoke-operator")) {
    using var scope = app.Services.CreateScope();
    var email = app.Configuration["Operations:Email"] ?? throw new InvalidOperationException("Set Operations:Email to the existing account email.");
    await OperationsService.SetOperatorAsync(scope.ServiceProvider, email, args.Contains("--grant-operator"));
    return;
}
if (args.Contains("--reconcile")) {
    using var scope = app.Services.CreateScope();
    var report = await scope.ServiceProvider.GetRequiredService<ReconciliationService>().CheckAsync(CancellationToken.None);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    Environment.ExitCode = report.Healthy ? 0 : 2;
    return;
}
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
app.MapGet("/", () => Results.Ok(new { name = "FinTech Wallet API", version = "0.5.0", health = "/health/live", documentation = "/openapi/v1.json (Development only)" }));
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
auth.MapPost("/resend-verification", async (EmailRequest r, AccountSecurityService service, CancellationToken ct) => {
    await service.RequestAsync(r.Email, ChallengeTokens.VerifyEmail, ct);
    return Results.Accepted(value: new { message = AccountSecurityService.GenericMessage });
});
auth.MapPost("/forgot-password", async (EmailRequest r, AccountSecurityService service, CancellationToken ct) => {
    await service.RequestAsync(r.Email, ChallengeTokens.ResetPassword, ct);
    return Results.Accepted(value: new { message = AccountSecurityService.GenericMessage });
});
auth.MapPost("/confirm-email", async (ConfirmEmailRequest r, AccountSecurityService service, CancellationToken ct) => {
    await service.ConfirmAsync(r, ct); return Results.NoContent();
});
auth.MapPost("/reset-password", async (ResetPasswordRequest r, AccountSecurityService service, CancellationToken ct) => {
    await service.ResetAsync(r, ct); return Results.NoContent();
});
var api = app.MapGroup("/api/v1").RequireAuthorization().RequireRateLimiting("wallet");
api.AddEndpointFilter<ValidationFilter>();
api.AddEndpointFilter(async (context, next) => {
    context.HttpContext.Response.Headers.CacheControl = "no-store";
    return await next(context);
});
api.MapGet("/auth/mfa/status", async (ClaimsPrincipal user, AppDbContext db, CancellationToken ct) => {
    var actor = UserId(user);
    var enabled = await db.Users.Where(x => x.Id == actor).Select(x => x.TwoFactorEnabled).SingleAsync(ct);
    var lockedUntil = await db.MfaProfiles.Where(x => x.UserId == actor).Select(x => x.LockedUntil).SingleOrDefaultAsync(ct);
    return Results.Ok(new { enabled, lockedUntil, recoveryCodesRemaining = await db.MfaRecoveryCodes.CountAsync(x => x.UserId == actor && x.UsedAt == null, ct) });
});
api.MapPost("/auth/mfa/enrollment", async (PasswordRequest r, ClaimsPrincipal user, MfaService service, CancellationToken ct) =>
    Results.Ok(await service.BeginAsync(UserId(user), SessionId(user), r.CurrentPassword, ct)));
api.MapPost("/auth/mfa/confirm", async (MfaConfirmRequest r, ClaimsPrincipal user, MfaService service, CancellationToken ct) =>
    Results.Ok(await service.ConfirmAsync(UserId(user), SessionId(user), r.Code, ct)));
api.MapPost("/auth/mfa/step-up", async (MfaProofRequest r, ClaimsPrincipal user, MfaService service, CancellationToken ct) => {
    await service.StepUpAsync(UserId(user), SessionId(user), r, ct); return Results.NoContent();
});
api.MapPost("/auth/mfa/disable", async (MfaProofRequest r, ClaimsPrincipal user, MfaService service, CancellationToken ct) => {
    await service.DisableAsync(UserId(user), SessionId(user), r, ct); return Results.NoContent();
});
api.MapPost("/auth/mfa/recovery-codes", async (MfaProofRequest r, ClaimsPrincipal user, MfaService service, CancellationToken ct) =>
    Results.Ok(await service.RegenerateAsync(UserId(user), SessionId(user), r, ct)));
api.MapPost("/operations/wallets/{id:guid}/unfreeze-requests", async (Guid id, ApprovalActionRequest r, ClaimsPrincipal user, WalletApprovalService service, CancellationToken ct) => {
    var item = await service.RequestAsync(UserId(user), SessionId(user), id, r, ct);
    return Results.Created($"/api/v1/operations/unfreeze-requests/{item.Id}", item);
}).RequireAuthorization("WalletOperator");
api.MapGet("/operations/unfreeze-requests", async (string? status, AppDbContext db, CancellationToken ct) => {
    if (status is not null && status is not ("Pending" or "Approved" or "Rejected" or "Cancelled" or "Expired" or "Invalidated"))
        throw new ApiException(400, "invalid_status", "Unsupported approval status.");
    var query = db.WalletApprovals.AsNoTracking();
    if (status is not null) query = query.Where(x => x.Status == status);
    return Results.Ok((await query.OrderByDescending(x => x.RequestedAt).ThenByDescending(x => x.Id).Take(100).ToListAsync(ct)).Select(WalletApprovalService.ToResponse));
}).RequireAuthorization("WalletOperator");
api.MapGet("/operations/unfreeze-requests/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) => {
    var item = await db.WalletApprovals.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    return item is null ? Results.NotFound() : Results.Ok(WalletApprovalService.ToResponse(item));
}).RequireAuthorization("WalletOperator");
api.MapPost("/operations/unfreeze-requests/{id:guid}/{decision}", async (Guid id, string decision, ApprovalActionRequest r, ClaimsPrincipal user, WalletApprovalService service, CancellationToken ct) =>
    Results.Ok(await service.DecideAsync(UserId(user), SessionId(user), id, decision, r, ct))).RequireAuthorization("WalletOperator");
api.MapGet("/auth/sessions", async (ClaimsPrincipal user, AppDbContext db, CancellationToken ct) => {
    var actor = UserId(user); var current = Guid.Parse(user.FindFirstValue("sid")!); var now = DateTimeOffset.UtcNow;
    return Results.Ok(await db.Sessions.AsNoTracking().Where(x => x.UserId == actor && x.RevokedAt == null && x.ExpiresAt > now && x.SecurityStamp == x.User.SecurityStamp)
        .OrderByDescending(x => x.CreatedAt).Take(100).Select(x => new SessionResponse(x.Id, x.CreatedAt, x.ExpiresAt, x.Id == current)).ToListAsync(ct));
});
api.MapDelete("/auth/sessions/{id:guid}", async (Guid id, ClaimsPrincipal user, AccountSecurityService service, CancellationToken ct) => {
    await service.RevokeAsync(UserId(user), id, ct); return Results.NoContent();
});
api.MapPost("/auth/logout-all", async (ClaimsPrincipal user, AccountSecurityService service, CancellationToken ct) => {
    await service.RevokeAllAsync(UserId(user), ct); return Results.NoContent();
});
api.MapPut("/operations/wallets/{id:guid}/state", async (Guid id, WalletStateRequest r, ClaimsPrincipal user, OperationsService service, CancellationToken ct) =>
    Results.Ok(await service.SetWalletStateAsync(UserId(user), SessionId(user), id, r, ct))).RequireAuthorization("WalletOperator");
api.MapGet("/operations/reconciliation", async (ReconciliationService service, CancellationToken ct) =>
    Results.Ok(await service.CheckAsync(ct))).RequireAuthorization("WalletOperator");
api.MapGet("/operations/monitoring", async (OperationsMonitoring service, CancellationToken ct) =>
    Results.Ok(await service.SummaryAsync(ct))).RequireAuthorization("WalletOperator");
api.MapGet("/operations/audit-events", async (int? page, int? pageSize, DateTimeOffset? from, DateTimeOffset? to, string? action, Guid? actorId, Guid? targetId, OperationsMonitoring service, CancellationToken ct) =>
    Results.Ok(await service.AuditAsync(page, pageSize, from, to, action, actorId, targetId, ct))).RequireAuthorization("WalletOperator");
api.MapGet("/operations/reconciliation-runs", async (int? page, int? pageSize, OperationsMonitoring service, CancellationToken ct) =>
    Results.Ok(await service.RunsAsync(page, pageSize, ct))).RequireAuthorization("WalletOperator");
api.MapGet("/operations/reconciliation-runs/{id:guid}", async (Guid id, OperationsMonitoring service, CancellationToken ct) =>
    Results.Ok(await service.RunAsync(id, ct))).RequireAuthorization("WalletOperator");
api.MapGet("/operations/email-jobs", async (string? state, int? page, int? pageSize, OperationsMonitoring service, CancellationToken ct) =>
    Results.Ok(await service.EmailJobsAsync(state, page, pageSize, ct))).RequireAuthorization("WalletOperator");
api.MapGet("/operations/email-status", async (AppDbContext db, CancellationToken ct) => {
    var now = DateTimeOffset.UtcNow;
    return Results.Ok(new { pending = await db.EmailOutbox.CountAsync(x => x.SentAt == null && x.FailedAt == null && x.ExpiresAt > now, ct),
        failed = await db.EmailOutbox.CountAsync(x => x.FailedAt != null, ct),
        expiredPending = await db.EmailOutbox.CountAsync(x => x.SentAt == null && x.FailedAt == null && x.ExpiresAt <= now, ct) });
}).RequireAuthorization("WalletOperator");
api.MapGet("/wallets/me", async (ClaimsPrincipal user, AppDbContext db, CancellationToken ct) => {
    var id = UserId(user);
    var wallet = await db.Wallets.AsNoTracking().Where(x => x.UserId == id)
        .Select(x => new WalletResponse(x.Id, x.Currency, x.Balance, x.IsFrozen)).SingleOrDefaultAsync(ct);
    return wallet is null ? Results.NotFound() : Results.Ok(wallet);
});
api.MapPost("/transfers/assess", async (TransferRequest r, ClaimsPrincipal user, TransferRiskService service, CancellationToken ct) =>
    Results.Ok(await service.AssessAsync(UserId(user), SessionId(user), r, ct)));
api.MapPost("/transfers/authorizations", async (TransferAuthorizationRequest r, HttpContext context, TransferRiskService service, CancellationToken ct) =>
    Results.Ok(await service.AuthorizeAsync(UserId(context.User), SessionId(context.User), r, context.Request.Headers["Idempotency-Key"].ToString(), ct)));
api.MapPost("/transfers", async (TransferRequest r, HttpContext context, TransferService service, CancellationToken ct) => {
    var result = await service.SendAsync(UserId(context.User), SessionId(context.User), r, context.Request.Headers["Idempotency-Key"].ToString(), ct);
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
api.MapGet("/transactions", async (int? page, int? pageSize, DateTimeOffset? from, DateTimeOffset? to, string? kind, string? direction, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) => {
    var p = page ?? 1; var size = pageSize ?? 20;
    if (p < 1 || p > 100_000 || size < 1 || size > 100) throw new ApiException(400, "invalid_pagination", "Page must be 1–100000; pageSize must be 1–100.");
    HistoryFilters.Validate(from, to, kind, direction);
    var actor = UserId(user);
    var walletIds = db.Wallets.Where(x => x.UserId == actor).Select(x => x.Id);
    var query = db.Transfers.AsNoTracking().Where(x => walletIds.Contains(x.SourceWalletId) || walletIds.Contains(x.DestinationWalletId));
    if (from.HasValue) query = query.Where(x => x.CreatedAt >= from.Value);
    if (to.HasValue) query = query.Where(x => x.CreatedAt < to.Value);
    if (kind is not null) query = query.Where(x => x.Kind == kind);
    if (direction == "incoming") query = query.Where(x => walletIds.Contains(x.DestinationWalletId));
    if (direction == "outgoing") query = query.Where(x => walletIds.Contains(x.SourceWalletId));
    var items = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Skip((p - 1) * size).Take(size + 1).ToListAsync(ct);
    return Results.Ok(new { page = p, pageSize = size, hasMore = items.Count > size, items = items.Take(size).Select(TransferService.ToResponse) });
});
app.Run();
static Guid SessionId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue("sid")!);
static Guid UserId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue("sub")!);
public partial class Program { }
