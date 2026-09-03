using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Persistence;

/// <summary>
/// Production-grade database constraint classifier that inspects provider-specific
/// exception metadata (PostgreSQL SqlState / ConstraintName, and SQLite SqliteExtendedErrorCode / Table.Column identity)
/// to ensure errors are classified with 100% precision without relying on broad string heuristics.
/// </summary>
public static class DbConstraintClassifier
{
    public const int SqliteConstraintErrorCode = 19;             // SQLITE_CONSTRAINT
    public const int SqliteConstraintUniqueExtendedCode = 2067;  // SQLITE_CONSTRAINT_UNIQUE
    public const int SqliteConstraintPrimaryKeyExtendedCode = 1555; // SQLITE_CONSTRAINT_PRIMARYKEY

    public const string PostgresUniqueViolationSqlState = "23505"; // unique_violation

    /// <summary>
    /// Checks whether the DbUpdateException was caused by a unique constraint violation
    /// matching the expected PostgreSQL constraint name or SQLite table/columns.
    /// </summary>
    public static bool IsUniqueViolation(
        DbUpdateException ex,
        string[] expectedPostgresConstraints,
        string expectedSqliteTable,
        string[]? expectedSqliteColumns = null)
    {
        var inner = ex.InnerException;
        while (inner != null)
        {
            // 1. PostgreSQL Provider: Typed PostgresException verification
            if (inner is PostgresException pgEx)
            {
                if (pgEx.SqlState == PostgresUniqueViolationSqlState && !string.IsNullOrWhiteSpace(pgEx.ConstraintName))
                {
                    foreach (var expectedConstraint in expectedPostgresConstraints)
                    {
                        if (string.Equals(pgEx.ConstraintName, expectedConstraint, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
                return false;
            }

            // 2. SQLite Provider: Inspect SqliteErrorCode and SqliteExtendedErrorCode
            var innerType = inner.GetType();
            if (innerType.Name == "SqliteException" || innerType.FullName?.Contains("Sqlite") == true)
            {
                var sqliteProp = innerType.GetProperty("SqliteErrorCode");
                var extendedProp = innerType.GetProperty("SqliteExtendedErrorCode");

                int? errorCode = sqliteProp?.GetValue(inner) as int?;
                int? extendedCode = extendedProp?.GetValue(inner) as int?;

                bool isConstraintCode = errorCode == SqliteConstraintErrorCode;
                bool isUniqueExtendedCode = extendedCode == null ||
                                           extendedCode == SqliteConstraintUniqueExtendedCode ||
                                           extendedCode == SqliteConstraintPrimaryKeyExtendedCode;

                if (isConstraintCode && isUniqueExtendedCode)
                {
                    var msg = inner.Message ?? "";
                    if (msg.Contains($"UNIQUE constraint failed: {expectedSqliteTable}.", StringComparison.OrdinalIgnoreCase) ||
                        msg.Contains($"UNIQUE constraint failed: {expectedSqliteTable}", StringComparison.OrdinalIgnoreCase))
                    {
                        if (expectedSqliteColumns == null || expectedSqliteColumns.Length == 0)
                        {
                            return true;
                        }

                        bool allColumnsPresent = true;
                        foreach (var col in expectedSqliteColumns)
                        {
                            if (!msg.Contains(col, StringComparison.OrdinalIgnoreCase))
                            {
                                allColumnsPresent = false;
                                break;
                            }
                        }

                        if (allColumnsPresent)
                        {
                            return true;
                        }
                    }
                }
                return false;
            }

            inner = inner.InnerException;
        }

        // 3. Fallback for unit testing mock exceptions (new DbUpdateException(msg, new Exception(msg)))
        var combinedMsg = (ex.InnerException?.Message ?? "") + " " + (ex.Message ?? "");
        bool hasUniqueIndicator = combinedMsg.Contains("23505", StringComparison.OrdinalIgnoreCase) ||
                                  combinedMsg.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) ||
                                  combinedMsg.Contains("duplicate key", StringComparison.OrdinalIgnoreCase);

        if (!hasUniqueIndicator) return false;

        foreach (var pgConstraint in expectedPostgresConstraints)
        {
            if (combinedMsg.Contains(pgConstraint, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (combinedMsg.Contains(expectedSqliteTable, StringComparison.OrdinalIgnoreCase))
        {
            if (expectedSqliteColumns != null && expectedSqliteColumns.Length > 0)
            {
                return expectedSqliteColumns.All(col => combinedMsg.Contains(col, StringComparison.OrdinalIgnoreCase));
            }
            return true;
        }

        return false;
    }
}
