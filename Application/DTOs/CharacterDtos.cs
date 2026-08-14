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
    DateTime CreatedAt
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
