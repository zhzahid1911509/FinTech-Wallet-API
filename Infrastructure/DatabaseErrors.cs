using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
namespace FinTechWalletAPI.Infrastructure;
public static class DatabaseErrors
{
    // EF's non-retrying SQL execution strategy can wrap a transient SqlException
    // in InvalidOperationException, including deadlocks raised during a query.
    public static int? SqlNumber(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is SqlException sql) return sql.Number;
        return null;
    }
    public static bool IsRetryableTransfer(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is DbUpdateConcurrencyException) return true;
        return SqlNumber(exception) is 1205 or 2601 or 2627;
    }
}
