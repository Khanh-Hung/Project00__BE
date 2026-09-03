namespace Domain.ValueObjects;

public sealed record CharacterPerceptionContext(
    DateTime EvaluatedAtUtc,
    Guid? CharacterId = null,
    string? CurrentActivity = null,
    string? Location = null
);
