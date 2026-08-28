namespace Domain.Enums;

/// <summary>
/// Deterministic priority hierarchy for character reactions.
/// Lower integer value indicates higher priority (1 = Critical Safety/Survival, 8 = Low-value System).
/// </summary>
public enum ReactionPriority
{
    CriticalSurvival = 1,
    DirectUserInteraction = 2,
    RelationshipAffecting = 3,
    GoalCritical = 4,
    SignificantActivityOutcome = 5,
    SocialEvent = 6,
    AmbientWorld = 7,
    LowValueSystem = 8
}
