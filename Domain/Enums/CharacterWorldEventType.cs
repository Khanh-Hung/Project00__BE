namespace Domain.Enums;

/// <summary>
/// Domain categories for world events perceivable by a character.
/// </summary>
public enum CharacterWorldEventType
{
    UserMessage,
    RelationshipChanged,
    GoalProgressed,
    GoalCompleted,
    ActivityCompleted,
    NewLocation,
    SocialInteraction,
    ExternalWorldEvent,
    SystemEvent
}
