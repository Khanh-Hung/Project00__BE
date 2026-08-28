using Domain.Enums;

namespace Domain.ValueObjects;

/// <summary>
/// Domain value object representing a character''s perception of a world event.
/// </summary>
public sealed record CharacterPerception(
    Guid CharacterId,
    Guid WorldEventId,
    PerceptionType PerceptionType,
    EventSalience Salience,
    EmotionalValence EmotionalValence,
    float Relevance,
    bool IsRelevant,
    string Reason
);
