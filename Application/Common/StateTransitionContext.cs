namespace Application.Common;

public sealed record StateTransitionContext(
    Guid ExecutionId,
    string SourceType,
    string? SourceId = null,
    string? Reason = null
);
