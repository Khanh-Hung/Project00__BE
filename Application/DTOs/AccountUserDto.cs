namespace Application.DTOs;

public sealed record AccountUserDto(
    Guid UserId,
    string? UserName,
    string? DisplayName,
    string? AvatarUrl
);
