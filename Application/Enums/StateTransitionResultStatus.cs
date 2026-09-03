namespace Application.Enums;

public enum StateTransitionResultStatus
{
    Applied = 1,
    AlreadyApplied = 2,
    ConcurrencyConflict = 3,
    InvalidState = 4,
    InvalidEvolutionTime = 5,
    NotFound = 6,
    IdempotencyConflict = 7
}
