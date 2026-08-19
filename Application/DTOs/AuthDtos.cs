namespace Application.DTOs;

public record UserDto(
    Guid Id,
    string Email,
    string UserName,
    string DisplayName,
    string AvatarUrl,
    DateTime CreatedAt,
    DateTime? LastUserNameChangedAt = null,
    bool CanChangeUserName = true,
    DateTime? NextUserNameChangeDate = null
);

public record RegisterRequest(
    string Email,
    string Password,
    string? UserName = null,
    string? DisplayName = null,
    string? AvatarUrl = null
);

public record LoginRequest(
    string Email,
    string Password
);

public record UpdateProfileRequest(
    string? DisplayName = null,
    string? AvatarUrl = null,
    string? UserName = null
);

public record AuthResponse(
    string Token,
    UserDto User
);
