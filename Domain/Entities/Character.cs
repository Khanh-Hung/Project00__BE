using Domain.Common;

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
    public Guid? CreatorId { get; private set; }

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
        Guid? creatorId = null)
    {
        Name = name;
        Title = title;
        AvatarUrl = avatarUrl;
        PersonalityPrompt = personalityPrompt;
        Greeting = greeting;
        Category = category;
        Tags = tags ?? [];
        IsPublic = isPublic;
        CreatorId = creatorId;
    }

    public void UpdateDetails(string name, string title, string avatarUrl, string personalityPrompt, string greeting, string category, List<string> tags)
    {
        Name = name;
        Title = title;
        AvatarUrl = avatarUrl;
        PersonalityPrompt = personalityPrompt;
        Greeting = greeting;
        Category = category;
        Tags = tags;
        Touch();
    }

    public void SetPublicStatus(bool isPublic)
    {
        IsPublic = isPublic;
        Touch();
    }
}
