using Domain.Common;
using Domain.Enums;

namespace Domain.Entities;

/// <summary>
/// Domain aggregate representing an event that occurred in the character''s world.
/// </summary>
public sealed class CharacterWorldEvent : BaseEntity
{
    public Guid CharacterId { get; private set; }
    public CharacterWorldEventType EventType { get; private set; }
    public string SourceType { get; private set; } = string.Empty;
    public string? SourceId { get; private set; }
    public DateTime OccurredAt { get; private set; }
    public string? PayloadJson { get; private set; }
    public string? CorrelationId { get; private set; }
    public long Version { get; private set; } = 1;

    private CharacterWorldEvent() { } // EF Core

    private CharacterWorldEvent(
        Guid id,
        Guid characterId,
        CharacterWorldEventType eventType,
        string sourceType,
        string? sourceId,
        DateTime occurredAt,
        string? payloadJson,
        string? correlationId)
    {
        Id = id;
        CharacterId = characterId;
        EventType = eventType;
        SourceType = sourceType;
        SourceId = sourceId;
        OccurredAt = occurredAt;
        PayloadJson = payloadJson;
        CorrelationId = correlationId;
    }

    public static CharacterWorldEvent Create(
        Guid characterId,
        CharacterWorldEventType eventType,
        string sourceType,
        string? sourceId = null,
        DateTime? occurredAt = null,
        string? payloadJson = null,
        string? correlationId = null,
        Guid? id = null)
    {
        if (characterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));

        if (string.IsNullOrWhiteSpace(sourceType))
            throw new ArgumentException("SourceType cannot be empty.", nameof(sourceType));

        var eventId = id ?? Guid.CreateVersion7();
        var time = occurredAt ?? DateTime.UtcNow;

        return new CharacterWorldEvent(
            id: eventId,
            characterId: characterId,
            eventType: eventType,
            sourceType: sourceType.Trim(),
            sourceId: sourceId?.Trim(),
            occurredAt: time,
            payloadJson: payloadJson,
            correlationId: correlationId?.Trim()
        );
    }
}
