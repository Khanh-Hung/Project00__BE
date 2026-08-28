namespace Domain.Enums;

/// <summary>
/// Origin source of a CharacterActivity.
/// Distinguishes explicit user-driven actions from autonomous character decisions and system triggers.
/// </summary>
public enum CharacterActivitySource
{
    UserInteraction = 0,
    Autonomous = 1,
    System = 2
}
