using Domain.Common;
using Domain.Enums;

namespace Domain.Entities;

public sealed class CharacterMemory : BaseEntity
{
    public Guid CharacterId { get; private set; }
    public Guid UserId { get; private set; }
    public Guid? SourceSessionId { get; private set; }

    public string Content { get; private set; } = string.Empty;
    public MemoryType Type { get; private set; }
    public int Importance { get; private set; }
    public decimal Confidence { get; private set; }
    public DateTime? LastAccessedAt { get; private set; }

    private CharacterMemory() { } // EF Core

    private CharacterMemory(
        Guid characterId,
        Guid userId,
        string content,
        MemoryType type,
        int importance,
        decimal confidence,
        Guid? sourceSessionId = null)
    {
        CharacterId = characterId;
        UserId = userId;
        Content = content;
        Type = type;
        Importance = importance;
        Confidence = confidence;
        SourceSessionId = sourceSessionId;
    }

    public static CharacterMemory Create(
        Guid characterId,
        Guid userId,
        string content,
        MemoryType type,
        int importance = 3,
        decimal confidence = 0.9m,
        Guid? sourceSessionId = null)
    {
        if (characterId == Guid.Empty)
        {
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));
        }

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("UserId cannot be empty.", nameof(userId));
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("Memory content cannot be null or empty.", nameof(content));
        }

        var trimmedContent = content.Trim();
        if (trimmedContent.Length > 1000)
        {
            trimmedContent = trimmedContent.Substring(0, 1000).Trim();
        }

        var clampedImportance = Math.Clamp(importance, 1, 5);
        var clampedConfidence = Math.Clamp(confidence, 0.0m, 1.0m);

        return new CharacterMemory(
            characterId,
            userId,
            trimmedContent,
            type,
            clampedImportance,
            clampedConfidence,
            sourceSessionId
        );
    }

    public void UpdateDetails(int? importance = null, decimal? confidence = null, string? updatedContent = null)
    {
        if (!string.IsNullOrWhiteSpace(updatedContent))
        {
            var trimmed = updatedContent.Trim();
            Content = trimmed.Length > 1000 ? trimmed.Substring(0, 1000).Trim() : trimmed;
        }

        if (importance.HasValue)
        {
            Importance = Math.Clamp(importance.Value, 1, 5);
        }

        if (confidence.HasValue)
        {
            Confidence = Math.Clamp(confidence.Value, 0.0m, 1.0m);
        }

        Touch();
    }

    public void MarkAccessed(DateTime timestamp)
    {
        LastAccessedAt = timestamp;
        Touch();
    }
}
