namespace Application.DTOs;

public record UserProfileDto(
    Guid Id,
    Guid UserId,
    string DisplayName,
    string? AvatarUrl,
    string? Bio,
    List<string> Interests,
    List<string> PersonalityTraits,
    string? StatusMessage,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);

public record UpdateUserProfileRequest(
    string DisplayName,
    string? AvatarUrl = null,
    string? Bio = null,
    List<string>? Interests = null,
    List<string>? PersonalityTraits = null,
    string? StatusMessage = null
);

public record ProactiveReachoutRequest(
    Guid CharacterId,
    Guid UserId
);

public record ProactiveReachoutResponse(
    Guid SessionId,
    Guid CharacterId,
    string CharacterName,
    string CharacterAvatar,
    string OpeningMessage,
    string MatchReason,
    DateTime CreatedAt
);

public record ProactiveAiReachoutResult(
    string OpeningMessage,
    string MatchReason
);
