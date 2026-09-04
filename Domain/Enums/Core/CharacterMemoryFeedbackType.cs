namespace Domain.Enums;

/// <summary>
/// Categorical semantic outcome of a character cognitive cycle.
/// Persisted natively in CharacterMemory.FeedbackType independent of salience (Importance).
/// </summary>
public enum CharacterMemoryFeedbackType
{
    NoActionTaken = 1,
    ActionFailed = 2,
    EventExperienced = 3,
    ActionCompleted = 4
}
