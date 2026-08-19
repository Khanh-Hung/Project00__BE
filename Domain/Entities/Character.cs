using Domain.Common;
using Domain.ValueObjects;

namespace Domain.Entities;

public class Character : BaseEntity
{
    public string Name { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public string AvatarUrl { get; private set; } = string.Empty;
    public string PersonalityPrompt { get; private set; } = string.Empty;
    public string Greeting { get; private set; } = string.Empty;
    public string Category { get; private set; } = string.Empty;
    public List<string> Tags { get; private set; } = [];
    public bool IsPublic { get; private set; } = true;
    public int DefaultAffectionScore { get; private set; }
    public string DefaultMood { get; private set; } = string.Empty;
    public string? CustomMilestonesJson { get; private set; }
    public CharacterBlueprint? Blueprint { get; private set; }

    private Character() { } // EF Core

    public Character(
        string name,
        string title,
        string avatarUrl,
        string personalityPrompt,
        string greeting,
        string category,
        List<string>? tags = null,
        bool isPublic = true,
        int defaultAffectionScore = 0,
        string? defaultMood = null,
        string? customMilestonesJson = null,
        CharacterBlueprint? blueprint = null)
    {
        Name = name;
        Title = title;
        AvatarUrl = avatarUrl;
        PersonalityPrompt = personalityPrompt;
        Greeting = greeting;
        Category = category;
        Tags = tags ?? [];
        IsPublic = isPublic;
        DefaultAffectionScore = Math.Clamp(defaultAffectionScore, -100, 100);
        DefaultMood = defaultMood ?? string.Empty;
        CustomMilestonesJson = customMilestonesJson;
        Blueprint = blueprint;
    }

    public void UpdateDetails(
        string name,
        string title,
        string avatarUrl,
        string personalityPrompt,
        string greeting,
        string category,
        List<string> tags,
        int? defaultAffectionScore = null,
        string? defaultMood = null,
        string? customMilestonesJson = null,
        CharacterBlueprint? blueprint = null,
        bool updateBlueprint = false)
    {
        Name = name;
        Title = title;
        AvatarUrl = avatarUrl;
        PersonalityPrompt = personalityPrompt;
        Greeting = greeting;
        Category = category;
        Tags = tags;
        if (defaultAffectionScore.HasValue)
        {
            DefaultAffectionScore = Math.Clamp(defaultAffectionScore.Value, -100, 100);
        }
        if (defaultMood != null)
        {
            DefaultMood = defaultMood;
        }
        if (customMilestonesJson != null)
        {
            CustomMilestonesJson = customMilestonesJson;
        }
        if (updateBlueprint || blueprint != null)
        {
            Blueprint = blueprint;
        }
        Touch();
    }

    public void SetBlueprint(CharacterBlueprint? blueprint)
    {
        Blueprint = blueprint;
        Touch();
    }

    public void SetPublicStatus(bool isPublic)
    {
        IsPublic = isPublic;
        Touch();
    }
}
