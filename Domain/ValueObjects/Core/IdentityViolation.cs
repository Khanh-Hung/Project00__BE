using Domain.Enums;

namespace Domain.ValueObjects;

/// <summary>
/// Domain value object capturing a specific identity or invariant violation.
/// </summary>
public sealed record IdentityViolation(
    ReferenceAuthorityScope Scope,
    string Code,
    string Description,
    bool IsCritical
);
