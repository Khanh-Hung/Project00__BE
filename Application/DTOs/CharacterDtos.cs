using Domain.ValueObjects;

namespace Application.DTOs;

public record RelationshipMilestoneDto(
    string Name,
    int MinScore,
    int MaxScore,
    string Description,
    string? Icon = null
);

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
    string? CreatorAvatar = null,
    int DefaultAffectionScore = 0,
    string? DefaultMood = null,
    List<RelationshipMilestoneDto>? CustomMilestones = null,
    CharacterBlueprint? Blueprint = null
);

public record CreateCharacterRequest(
    string Name,
    string Title,
    string AvatarUrl,
    string PersonalityPrompt,
    string Greeting,
    string Category,
    List<string>? Tags = null,
    bool IsPublic = true,
    int DefaultAffectionScore = 0,
    string? DefaultMood = null,
    List<RelationshipMilestoneDto>? CustomMilestones = null,
    CharacterBlueprint? Blueprint = null
);

public record UpdateCharacterRequest(
    string Name,
    string Title,
    string AvatarUrl,
    string PersonalityPrompt,
    string Greeting,
    string Category,
    List<string>? Tags = null,
    bool IsPublic = true,
    int? DefaultAffectionScore = null,
    string? DefaultMood = null,
    List<RelationshipMilestoneDto>? CustomMilestones = null,
    CharacterBlueprint? Blueprint = null
);

public record GenerateCharacterAIRequest(
    string Idea,
    string? Category = null
);

public record GeneratedCharacterDto(
    string Name,
    string Title,
    string Category,
    string PersonalityPrompt,
    string Greeting,
    List<string> Tags,
    int DefaultAffectionScore = 0,
    string? DefaultMood = null,
    List<RelationshipMilestoneDto>? CustomMilestones = null,
    CharacterBlueprint? Blueprint = null
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
