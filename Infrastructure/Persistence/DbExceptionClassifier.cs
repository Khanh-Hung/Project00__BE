using System.Data.Common;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence;

/// <summary>
/// Classifies database, EF Core, and network/IO exceptions into transient (retryable) vs permanent (non-retryable).
/// Fails closed: any unclassified or unknown exception is considered non-transient.
/// </summary>
public static class DbExceptionClassifier
{
    public static bool IsTransient(Exception? ex)
    {
        if (ex == null) return false;

        // 1. EF Core concurrency conflicts (optimistic concurrency / row version mismatch)
        if (ex is DbUpdateConcurrencyException)
        {
            return true;
        }

        // 2. Network, Socket, IO, and Timeout exceptions (transient connectivity/storage interruptions)
        if (ex is TimeoutException or SocketException or HttpRequestException or IOException)
        {
            return true;
        }

        // 3. Database Provider-specific SQLSTATE and Error Codes
        if (ex is DbException dbEx)
        {
            // PostgreSQL SQLSTATE classification
            var sqlState = dbEx.GetType().GetProperty("SqlState")?.GetValue(dbEx)?.ToString();
            if (!string.IsNullOrEmpty(sqlState))
            {
                // Transient PostgreSQL codes:
                // 40001 = serialization_failure
                // 40P01 = deadlock_detected
                // 55P03 = lock_not_available
                // 57P01 = admin_shutdown
                // 08xxx = connection exceptions
                if (sqlState is "40001" or "40P01" or "55P03" or "57P01" || sqlState.StartsWith("08", StringComparison.Ordinal))
                {
                    return true;
                }

                // Permanent PostgreSQL codes (23xxx = integrity constraint violation, 42xxx = syntax/schema, 28xxx = auth, 22xxx = data error)
                return false;
            }

            // SQLite Error Code classification: 5 = SQLITE_BUSY, 6 = SQLITE_LOCKED
            var sqliteCode = dbEx.GetType().GetProperty("SqliteErrorCode")?.GetValue(dbEx);
            if (sqliteCode is int code && (code == 5 || code == 6))
            {
                return true;
            }

            return false;
        }

        // 4. Recursive inspection of InnerException
        if (ex.InnerException != null)
        {
            return IsTransient(ex.InnerException);
        }

        // 5. Fail-Closed Default: all other unknown exception types are NON-transient
        return false;
    }
}
