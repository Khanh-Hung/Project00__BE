namespace Application.Abstractions.Auth;

public interface ICurrentUserProvider
{
    string? CurrentUserId { get; }
}
