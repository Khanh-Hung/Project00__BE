using Domain.Enums;
using Domain.ValueObjects;

namespace Application.DTOs;

public record RelationshipMilestoneDto(
    string Name,
    int MinScore,
    int MaxScore,
    string Description,
    string? Icon = null
);

public record GeneratedLorebookDto(
    string Title,
    string Content,
    List<string> Keywords,
    LorebookCategory Category = LorebookCategory.Other,
    bool IsConstant = false,
    int Priority = 100
);

public record CharacterDto(
    Guid Id,
    string Name,
    string Title,
    string AvatarUrl,
    string PersonalityPrompt,
    string Greeting,
    string Category = "",
    List<string>? Tags = null,
    bool IsPublic = true,
    DateTime CreatedAt = default,
    string? CreatedBy = null,
    string? CreatorName = null,
    string? CreatorUserName = null,
    string? CreatorAvatar = null,
    int DefaultAffectionScore = 0,
    string? DefaultMood = null,
    List<RelationshipMilestoneDto>? CustomMilestones = null,
    CharacterBlueprint? Blueprint = null,
    CharacterVisualIdentity? VisualIdentity = null,
    CharacterVoiceProfile? VoiceProfile = null,
    string? WorldName = null,
    string? WorldDescription = null,
    WorldGenre WorldGenre = WorldGenre.MundaneSliceOfLife,
    string? CustomPhysicsRules = null
);

public record CreateCharacterRequest(
    string Name,
    string Title,
    string AvatarUrl,
    string PersonalityPrompt,
    string Category = "",
    string Greeting = "",
    List<string>? Tags = null,
    bool IsPublic = true,
    int DefaultAffectionScore = 0,
    string? DefaultMood = null,
    List<RelationshipMilestoneDto>? CustomMilestones = null,
    CharacterBlueprint? Blueprint = null,
    CharacterVisualIdentity? VisualIdentity = null,
    CharacterVoiceProfile? VoiceProfile = null,
    string? WorldName = null,
    string? WorldDescription = null,
    WorldGenre WorldGenre = WorldGenre.MundaneSliceOfLife,
    string? CustomPhysicsRules = null,
    List<GeneratedLorebookDto>? InitialLorebookEntries = null
);

public record UpdateCharacterRequest(
    string Name,
    string Title,
    string AvatarUrl,
    string PersonalityPrompt,
    string Category = "",
    string Greeting = "",
    List<string>? Tags = null,
    bool IsPublic = true,
    int? DefaultAffectionScore = null,
    string? DefaultMood = null,
    List<RelationshipMilestoneDto>? CustomMilestones = null,
    CharacterBlueprint? Blueprint = null,
    CharacterVisualIdentity? VisualIdentity = null,
    CharacterVoiceProfile? VoiceProfile = null,
    string? WorldName = null,
    string? WorldDescription = null,
    WorldGenre? WorldGenre = null,
    string? CustomPhysicsRules = null
);

public record GenerateCharacterAIRequest(
    string Idea,
    string? Category = null
);

public record GeneratedCharacterDto(
    string Name,
    string Title,
    string Category = "",
    string PersonalityPrompt = "",
    string Greeting = "",
    List<string>? Tags = null,
    int DefaultAffectionScore = 0,
    string? DefaultMood = null,
    List<RelationshipMilestoneDto>? CustomMilestones = null,
    CharacterVisualIdentity? VisualIdentity = null,
    CharacterVoiceProfile? VoiceProfile = null,
    CharacterBlueprint? Blueprint = null,
    string? WorldName = null,
    string? WorldDescription = null,
    WorldGenre WorldGenre = WorldGenre.MundaneSliceOfLife,
    string? CustomPhysicsRules = null,
    List<GeneratedLorebookDto>? InitialLorebookEntries = null
);

public record GenerateAvatarRequest
{
    public string? Name { get; init; }
    public string? Title { get; init; }
    public string? Category { get; init; }
    public string? PersonalityPrompt { get; init; }
    public string? Idea { get; init; }
    public WorldGenre? WorldGenre { get; init; }
    public CharacterVisualIdentity? VisualIdentity { get; init; }

    public GenerateAvatarRequest() { }

    public GenerateAvatarRequest(
        string? name = null,
        string? title = null,
        string? category = null,
        string? personalityPrompt = null,
        string? idea = null,
        WorldGenre? worldGenre = null,
        CharacterVisualIdentity? visualIdentity = null)
    {
        Name = name;
        Title = title;
        Category = category;
        PersonalityPrompt = personalityPrompt;
        Idea = idea;
        WorldGenre = worldGenre;
        VisualIdentity = visualIdentity;
    }
}

public record GenerateAvatarResponse(
    string ImageUrl,
    string RevisedPrompt,
    string? AvatarUrl = null,
    string? FullBodyUrl = null,
    string? FullBodyPrompt = null
);
