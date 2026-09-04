namespace Domain.ValueObjects;

public sealed record CharacterPerceptionContext(
    DateTime EvaluatedAtUtc,
    Guid CharacterId,
    string? CurrentActivity = null,
    string? Location = null,
    CharacterPerceptionStimulus? Stimulus = null,
    CharacterMemoryContext? MemoryContext = null
);
