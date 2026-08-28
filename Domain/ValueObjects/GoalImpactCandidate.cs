namespace Domain.ValueObjects;

/// <summary>
/// Value object representing an impact on an active character goal resulting from an event reaction.
/// </summary>
public sealed record GoalImpactCandidate(
    Guid GoalId,
    double ContributionValue,
    string Reason
);
