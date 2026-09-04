using System.Text.Json;
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
    public string? EmbeddingJson { get; private set; }

    public CharacterMemoryFeedbackType? FeedbackType { get; private set; }
    public string? FeedbackFingerprint { get; private set; }

    private CharacterMemory() { } // EF Core

    private CharacterMemory(
        Guid characterId,
        Guid userId,
        string content,
        MemoryType type,
        int importance,
        decimal confidence,
        Guid? sourceSessionId = null,
        string? embeddingJson = null,
        CharacterMemoryFeedbackType? feedbackType = null,
        string? feedbackFingerprint = null)
    {
        CharacterId = characterId;
        UserId = userId;
        Content = content;
        Type = type;
        Importance = importance;
        Confidence = confidence;
        SourceSessionId = sourceSessionId;
        EmbeddingJson = embeddingJson;
        FeedbackType = feedbackType;
        FeedbackFingerprint = feedbackFingerprint;
    }

    public static CharacterMemory Create(
        Guid characterId,
        Guid userId,
        string content,
        MemoryType type,
        int importance = 3,
        decimal confidence = 0.9m,
        Guid? sourceSessionId = null,
        string? embeddingJson = null,
        CharacterMemoryFeedbackType? feedbackType = null,
        string? feedbackFingerprint = null)
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
            throw new ArgumentException("Memory content cannot exceed 1000 characters.", nameof(content));
        }

        if (importance is < 1 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(importance), "Importance must be between 1 and 5.");
        }

        if (confidence is < 0.0m or > 1.0m)
        {
            throw new ArgumentOutOfRangeException(nameof(confidence), "Confidence must be between 0.0 and 1.0.");
        }

        return new CharacterMemory(
            characterId,
            userId,
            trimmedContent,
            type,
            importance,
            confidence,
            sourceSessionId,
            embeddingJson,
            feedbackType,
            feedbackFingerprint?.Trim()
        );
    }

    public void SetEmbedding(float[]? embedding)
    {
        EmbeddingJson = embedding != null && embedding.Length > 0
            ? JsonSerializer.Serialize(embedding)
            : null;
        Touch();
    }

    public float[]? GetEmbedding()
    {
        if (string.IsNullOrWhiteSpace(EmbeddingJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<float[]>(EmbeddingJson);
        }
        catch
        {
            return null;
        }
    }

    public void UpdateDetails(int? importance = null, decimal? confidence = null, string? updatedContent = null)
    {
        if (updatedContent != null)
        {
            if (string.IsNullOrWhiteSpace(updatedContent))
            {
                throw new ArgumentException("Updated content cannot be empty.", nameof(updatedContent));
            }
            var trimmed = updatedContent.Trim();
            if (trimmed.Length > 1000)
            {
                throw new ArgumentException("Memory content cannot exceed 1000 characters.", nameof(updatedContent));
            }
            Content = trimmed;
        }

        if (importance.HasValue)
        {
            if (importance.Value is < 1 or > 5)
            {
                throw new ArgumentOutOfRangeException(nameof(importance), "Importance must be between 1 and 5.");
            }
            Importance = importance.Value;
        }

        if (confidence.HasValue)
        {
            if (confidence.Value is < 0.0m or > 1.0m)
            {
                throw new ArgumentOutOfRangeException(nameof(confidence), "Confidence must be between 0.0 and 1.0.");
            }
            Confidence = confidence.Value;
        }

        Touch();
    }

    public void MarkAccessed(DateTime timestamp)
    {
        LastAccessedAt = timestamp;
        Touch();
    }
}
