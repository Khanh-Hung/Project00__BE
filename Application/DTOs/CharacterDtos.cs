namespace Application.DTOs;

public record CharacterDto(
    Guid Id,
    string Name,
    string Title,
    string AvatarUrl,
    string PersonalityPrompt,
    string Greeting,
    string Category,
    List<string> Tags,
    bool IsPublic,
    DateTime CreatedAt,
    string? CreatedBy = null,
    string? CreatorName = null,
    string? CreatorUserName = null,
    string? CreatorAvatar = null
);

public record CreateCharacterRequest(
    string Name,
    string Title,
    string AvatarUrl,
    string PersonalityPrompt,
    string Greeting,
    string Category,
    List<string>? Tags = null,
    bool IsPublic = true
);

public record UpdateCharacterRequest(
    string Name,
    string Title,
    string AvatarUrl,
    string PersonalityPrompt,
    string Greeting,
    string Category,
    List<string>? Tags = null,
    bool IsPublic = true
);

public record GenerateCharacterAiRequest(
    string Idea,
    string? Category = null
);

public record GeneratedCharacterDto(
    string Name,
    string Title,
    string Category,
    string PersonalityPrompt,
    string Greeting,
    List<string> Tags
);

public record GenerateAvatarRequest(
    string? Name = null,
    string? Title = null,
    string? Category = null,
    string? PersonalityPrompt = null,
    string? Idea = null
);

public record GenerateAvatarResponse(
    string AvatarUrl,
    string Prompt
);
