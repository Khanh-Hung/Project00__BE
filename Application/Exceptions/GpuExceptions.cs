namespace Application.Exceptions;

/// <summary>
/// Transient GPU error that should be retried with exponential backoff
/// (e.g. HTTP 408 Timeout, 429 Rate Limit, 500/502/503/504 Server Error, Network Drop).
/// </summary>
public class GpuTransientException : Exception
{
    public int? StatusCode { get; }

    public GpuTransientException(string message, int? statusCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }
}

/// <summary>
/// Non-transient GPU error that indicates invalid data or permanent failure and should NOT be retried
/// (e.g. HTTP 400 Bad Request, 404 Reference Image Missing, 422 Validation Error).
/// </summary>
public class GpuNonTransientException : Exception
{
    public int? StatusCode { get; }

    public GpuNonTransientException(string message, int? statusCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }
}
