using System.Net;

namespace Application.Exceptions;

public class AccountServiceException : Exception
{
    public HttpStatusCode? StatusCode { get; }

    public AccountServiceException(string message, HttpStatusCode? statusCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }
}

public class AccountServiceUnavailableException : AccountServiceException
{
    public AccountServiceUnavailableException(string message, Exception? innerException = null)
        : base(message, HttpStatusCode.ServiceUnavailable, innerException)
    {
    }
}
