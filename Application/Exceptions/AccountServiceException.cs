namespace Application.Exceptions;

public class AccountServiceException : Exception
{
    public int? StatusCode { get; }

    public AccountServiceException(string message, int? statusCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }
}

public class AccountServiceUnavailableException : AccountServiceException
{
    public AccountServiceUnavailableException(string message, Exception? innerException = null)
        : base(message, 503, innerException)
    {
    }
}
