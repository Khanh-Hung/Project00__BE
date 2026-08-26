namespace Application.DTOs;

/// <summary>
/// Authoritative work item dispatched through the generation queue to bounded GPU workers.
/// Carries the complete, immutable scene generation payload (including canonical visual snapshot, user ID, and request ID)
/// along with the durable outbox message identifier.
/// </summary>
public sealed record GenerationWorkItem(
    SceneImageGenerationOutboxPayload Payload,
    Guid OutboxId,
    DateTime EnqueuedAt,
    int Priority = 0
);
