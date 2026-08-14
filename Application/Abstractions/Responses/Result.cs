using System.Text.Json.Serialization;

namespace Application.Abstractions.Responses;

public class Result<T>
{
    public bool IsSuccess { get; }
    public int? StatusCode { get; }
    public List<string> Errors { get; }
    public T Value { get; }

    [JsonConstructor]
    private Result(bool isSuccess, T value, List<string> errors, int? statusCode = null)
    {
        IsSuccess = isSuccess;
        StatusCode = statusCode;
        Value = value;
        Errors = errors ?? new List<string>();
    }

    public static Result<T> Success(T value)
        => new(true, value, new List<string>());

    public static Result<T> Failure(params string[] errors)
    {
        if (errors == null || errors.Length == 0)
            throw new ArgumentException("Errors cannot be empty");

        return new(false, default!, errors.ToList());
    }

    public static Result<T> Failure(int statusCode, params string[] errors)
    {
        if (errors == null || errors.Length == 0)
            throw new ArgumentException("Errors cannot be empty");

        return new(false, default!, errors.ToList(), statusCode);
    }
}
