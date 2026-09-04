namespace Domain.Enums;

/// <summary>
/// Social classification of a relationship.
/// In PR48, this represents categorical social state independent of numeric dimensions.
/// </summary>
public enum RelationshipType
{
    Unknown = 0,
    Stranger = 1,
    Acquaintance = 2,
    Friend = 3,
    CloseFriend = 4,
    Romantic = 5,
    Partner = 6,
    Family = 7,
    Rival = 8,
    Enemy = 9
}
