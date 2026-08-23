using System.Data.Common;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence;

public static class DbExceptionClassifier
{
    public static bool IsTransient(Exception? ex)
    {
        if (ex == null) return false;

        // 1. EF Core concurrency conflicts & transient provider exceptions
        if (ex is DbUpdateConcurrencyException)
        {
            return true;
        }

        // 2. IO, Network, Timeouts, Socket interruptions
        if (ex is IOException or HttpRequestException or TimeoutException or SocketException)
        {
            return true;
        }

        // 3. Explicit Non-Transient domain exceptions are never transient
        if (ex is Application.Exceptions.GpuNonTransientException || ex.GetType().Name.Contains("NonTransient", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 4. Explicit Transient domain exceptions are always transient
        if (ex is Application.Exceptions.GpuTransientException || ex.GetType().Name.Contains("Transient", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 5. Database Provider-specific transient error codes
        if (ex is DbException dbEx)
        {
            // Check for PostgreSQL SQLSTATE
            var sqlState = dbEx.GetType().GetProperty("SqlState")?.GetValue(dbEx)?.ToString();
            if (!string.IsNullOrEmpty(sqlState))
            {
                // 40001 (serialization_failure), 40P01 (deadlock_detected), 55P03 (lock_not_available), 57P01 (admin_shutdown), 08xxx (connection)
                if (sqlState is "40001" or "40P01" or "55P03" or "57P01" || sqlState.StartsWith("08", StringComparison.Ordinal))
                {
                    return true;
                }

                // Known permanent PostgreSQL codes: 23xxx (integrity/constraint), 42xxx (syntax/schema), 28xxx (auth), 22xxx (data)
                return false;
            }

            // Check for SQLite Error Code: 5 (SQLITE_BUSY), 6 (SQLITE_LOCKED)
            var sqliteCode = dbEx.GetType().GetProperty("SqliteErrorCode")?.GetValue(dbEx);
            if (sqliteCode is int code && (code == 5 || code == 6))
            {
                return true;
            }

            // Fallback message heuristics for transient lock / busy / connection errors
            var dbMsg = dbEx.Message.ToLowerInvariant();
            if (dbMsg.Contains("busy") || dbMsg.Contains("locked") || dbMsg.Contains("timeout") || dbMsg.Contains("deadlock") || dbMsg.Contains("connection"))
            {
                return true;
            }

            return false;
        }

        // 6. Generic message heuristics for transient storage / network errors
        var message = ex.Message.ToLowerInvariant();
        if (message.Contains("storage") || message.Contains("network") || message.Contains("timeout") || message.Contains("transient") || message.Contains("temporar"))
        {
            return true;
        }

        // 7. Check InnerException recursively
        if (ex.InnerException != null)
        {
            return IsTransient(ex.InnerException);
        }

        return false;
    }
}
