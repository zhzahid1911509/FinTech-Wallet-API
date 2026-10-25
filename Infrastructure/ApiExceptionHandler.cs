using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
namespace FinTechWalletAPI.Infrastructure;
public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken token)
    {
        var (status, code, title) = exception switch {
            ApiException e => (e.Status, e.Code, e.Message),
            BadHttpRequestException => (400, "invalid_request", "The request is invalid."),
            DbUpdateConcurrencyException => (409, "concurrency_conflict", "The resource changed. Retry the operation."),
            DbUpdateException e when e.InnerException is SqlException { Number: 2601 or 2627 }
                => (409, "duplicate_resource", "The resource already exists."),
            SqlException or DbUpdateException => (503, "database_unavailable", "The operation could not complete. Retry later."),
            _ => (500, "internal_error", "An unexpected error occurred.")
        };
        // Do not serialize request bodies, credentials, or database exception messages to clients/logs.
        if (status >= 500) logger.LogError("Request {TraceId} failed with {ExceptionType}", context.TraceIdentifier, exception.GetType().Name);
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new ProblemDetails {
            Status = status, Title = title, Type = "about:blank",
            Extensions = { ["code"] = code, ["traceId"] = context.TraceIdentifier }
        }, options: (System.Text.Json.JsonSerializerOptions?)null, contentType: "application/problem+json", cancellationToken: token);
        return true;
    }
}
