using System.Text.Json;
using FinTechWalletAPI.Data;
using FinTechWalletAPI.Models;
using MailKit.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;
namespace FinTechWalletAPI.Services;

public sealed class EmailSettings
{
    public string Mode { get; set; } = "Pickup";
    public bool WorkerEnabled { get; set; } = true;
    public string From { get; set; } = "wallet@example.test";
    public string PickupDirectory { get; set; } = "App_Data/mail";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string Tls { get; set; } = "StartTls";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public int MaxAttempts { get; set; } = 5;
}
public sealed record EmailPayload(string To, string Subject, string Text);
public sealed class EmailEnvelope(IDataProtectionProvider protection)
{
    private readonly IDataProtector protector = protection.CreateProtector("FinTechWallet.EmailOutbox.v1");
    public string Protect(EmailPayload payload) => protector.Protect(JsonSerializer.Serialize(payload));
    public EmailPayload Unprotect(string payload) => JsonSerializer.Deserialize<EmailPayload>(protector.Unprotect(payload))
        ?? throw new InvalidOperationException("Invalid email envelope.");
}
public interface IEmailTransport
{
    Task SendAsync(Guid id, EmailPayload payload, CancellationToken ct);
}
public sealed class EmailTransport(IOptions<EmailSettings> settings, IHostEnvironment environment) : IEmailTransport
{
    public async Task SendAsync(Guid id, EmailPayload payload, CancellationToken ct)
    {
        var options = settings.Value;
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(options.From));
        message.To.Add(MailboxAddress.Parse(payload.To));
        message.Subject = payload.Subject;
        message.MessageId = $"{id:N}@fintech-wallet.local";
        message.Body = new TextPart("plain") { Text = payload.Text };
        if (options.Mode == "Pickup")
        {
            if (!environment.IsDevelopment()) throw new InvalidOperationException("Pickup delivery is Development-only.");
            var directory = Path.GetFullPath(options.PickupDirectory, environment.ContentRootPath);
            Directory.CreateDirectory(directory);
            var destination = Path.Combine(directory, $"{id:N}.eml");
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await message.WriteToAsync(temporary, ct); File.Move(temporary, destination, overwrite: true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return;
        }
        using var smtp = new MailKit.Net.Smtp.SmtpClient { Timeout = 20000 };
        await smtp.ConnectAsync(options.Host, options.Port,
            options.Tls == "SslOnConnect" ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, ct);
        if (!string.IsNullOrEmpty(options.Username)) await smtp.AuthenticateAsync(options.Username, options.Password, ct);
        await smtp.SendAsync(message, ct);
        await smtp.DisconnectAsync(true, ct);
    }
}
public sealed class EmailDispatcher(AppDbContext db, EmailEnvelope envelope, IEmailTransport transport,
    IOptions<EmailSettings> settings, ILogger<EmailDispatcher> logger)
{
    // Optimistic atomic claim, shared by all worker processes. Sending is outside a DB transaction.
    public async Task<bool> DispatchOneAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var candidate = await db.EmailOutbox.AsNoTracking().Where(x => x.SentAt == null && x.FailedAt == null &&
            x.NextAttemptAt <= now && (x.LeaseUntil == null || x.LeaseUntil < now))
            .OrderBy(x => x.CreatedAt).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
        if (candidate is null) return false;
        var lease = Guid.NewGuid();
        var claimed = await db.EmailOutbox.Where(x => x.Id == candidate && x.SentAt == null && x.FailedAt == null &&
            x.NextAttemptAt <= now && (x.LeaseUntil == null || x.LeaseUntil < now))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseId, lease)
                .SetProperty(x => x.LeaseUntil, now.AddMinutes(2)).SetProperty(x => x.Attempts, x => x.Attempts + 1), ct);
        if (claimed == 0) return true;
        var item = await db.EmailOutbox.AsNoTracking().SingleAsync(x => x.Id == candidate, ct);
        var owned = db.EmailOutbox.Where(x => x.Id == item.Id && x.LeaseId == lease);
        if (item.ExpiresAt <= now || item.Attempts > settings.Value.MaxAttempts)
        {
            await owned.ExecuteUpdateAsync(s => s.SetProperty(x => x.FailedAt, now)
                .SetProperty(x => x.ProtectedPayload, "").SetProperty(x => x.LastErrorType, "ExpiredOrExhausted")
                .SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null).SetProperty(x => x.LeaseId, (Guid?)null), ct);
            return true;
        }
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            await transport.SendAsync(item.Id, envelope.Unprotect(item.ProtectedPayload), deadline.Token);
            await owned.ExecuteUpdateAsync(s => s.SetProperty(x => x.SentAt, DateTimeOffset.UtcNow)
                .SetProperty(x => x.ProtectedPayload, "").SetProperty(x => x.LastErrorType, (string?)null)
                .SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null).SetProperty(x => x.LeaseId, (Guid?)null), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var failed = item.Attempts >= settings.Value.MaxAttempts;
            await owned.ExecuteUpdateAsync(s => s.SetProperty(x => x.NextAttemptAt, now.AddSeconds(Math.Min(300, 15 * (1 << Math.Min(item.Attempts, 4)))))
                .SetProperty(x => x.FailedAt, failed ? now : (DateTimeOffset?)null)
                .SetProperty(x => x.ProtectedPayload, failed ? "" : item.ProtectedPayload)
                .SetProperty(x => x.LastErrorType, ex.GetType().Name)
                .SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null).SetProperty(x => x.LeaseId, (Guid?)null), ct);
            logger.LogWarning("Email job {JobId} failed ({ErrorType}); attempt {Attempt}", item.Id, ex.GetType().Name, item.Attempts);
        }
        return true;
    }
}
public sealed class EmailWorker(IServiceScopeFactory scopes, IOptions<EmailSettings> settings, ILogger<EmailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Value.WorkerEnabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = false;
            try {
                using var scope = scopes.CreateScope();
                worked = await scope.ServiceProvider.GetRequiredService<EmailDispatcher>().DispatchOneAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning("Email worker unavailable ({ErrorType})", ex.GetType().Name); }
            try { await Task.Delay(worked ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(10), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
