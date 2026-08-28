using Domain.Common;
using Domain.Enums;

namespace Domain.Entities;

public sealed class LorebookEntry : BaseEntity
{
    public Guid? CharacterId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string Content { get; private set; } = string.Empty;
    public List<string> Keywords { get; private set; } = [];
    public LorebookCategory Category { get; private set; } = LorebookCategory.Other;
    public bool IsConstant { get; private set; }
    public int Priority { get; private set; } = 100;
    public bool IsEnabled { get; private set; } = true;

    private LorebookEntry() { } // EF Core

    public LorebookEntry(
        Guid? characterId,
        string title,
        string content,
        List<string>? keywords = null,
        LorebookCategory category = LorebookCategory.Other,
        bool isConstant = false,
        int priority = 100,
        bool isEnabled = true)
    {
        CharacterId = characterId;
        Title = title;
        Content = content;
        Keywords = keywords ?? [];
        Category = category;
        IsConstant = isConstant;
        Priority = priority;
        IsEnabled = isEnabled;
    }

    public void Update(
        string title,
        string content,
        List<string>? keywords,
        LorebookCategory category,
        bool isConstant,
        int priority,
        bool isEnabled)
    {
        Title = title;
        Content = content;
        Keywords = keywords ?? [];
        Category = category;
        IsConstant = isConstant;
        Priority = priority;
        IsEnabled = isEnabled;
        Touch();
    }

    public void SetEnabled(bool enabled)
    {
        IsEnabled = enabled;
        Touch();
    }
}
