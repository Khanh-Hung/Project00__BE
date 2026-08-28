namespace Domain.Enums;

/// <summary>
/// Domain classification of how a character perceives an event.
/// </summary>
public enum PerceptionType
{
    PositiveSocialFeedback,
    NegativeSocialFeedback,
    UrgentWarning,
    EnvironmentalChange,
    GoalMilestoneReached,
    RoutineActivityOutcome,
    AmbientObservation,
    SystemNotice
}
