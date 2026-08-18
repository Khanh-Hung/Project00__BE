namespace Application.DTOs;

public record UserDto(
    Guid Id,
    string Email,
    string UserName,
    string AvatarUrl,
    DateTime CreatedAt
);

public record RegisterRequest(
    string Email,
    string Password,
    string? UserName = null,
    string? AvatarUrl = null
);

public record LoginRequest(
    string Email,
    string Password
);

public record AuthResponse(
    string Token,
    UserDto User
);
