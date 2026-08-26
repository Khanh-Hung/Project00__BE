using Domain.Abstractions;
using Domain.Common;
using Domain.Enums;

namespace Domain.Events;

public sealed record GenerationJobRequestedEvent(
    Guid JobId,
    Guid SessionId,
    Guid TurnId,
    int SceneRevision,
    Guid GenerationRequestId
) : IDomainEvent;

public sealed record GenerationAttemptStartedEvent(
    Guid JobId,
    Guid AttemptId,
    int AttemptNumber,
    long DerivedSeed,
    string WorkerId
) : IDomainEvent;

public sealed record GenerationAttemptEvaluatedEvent(
    Guid JobId,
    Guid AttemptId,
    int AttemptNumber,
    float? IdentitySimilarity,
    float? FeatureScore,
    IdentityStatus Status
) : IDomainEvent;

public sealed record GenerationJobAcceptedEvent(
    Guid JobId,
    Guid AcceptedAttemptId,
    Guid ArtifactId,
    string ImageUrl,
    bool IsCurrent
) : IDomainEvent;

public sealed record GenerationJobQuarantinedEvent(
    Guid JobId,
    Guid? LastAttemptId,
    string Reason
) : IDomainEvent;

public sealed record GenerationJobFailedEvent(
    Guid JobId,
    string ErrorMessage,
    bool IsRetryable
) : IDomainEvent;
