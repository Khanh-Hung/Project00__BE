namespace Domain.Enums;

/// <summary>
/// Identifies the target entity type for a social relationship.
/// PR48 MVP primarily supports User, with extensibility for future entities.
/// </summary>
public enum RelationshipTargetType
{
    User = 1,
    Character = 2,
    NPC = 3,
    Organization = 4,
    Location = 5
}
