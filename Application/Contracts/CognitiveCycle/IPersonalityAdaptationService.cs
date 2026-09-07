using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Application.Contracts.CognitiveCycle;

/// <summary>
/// Result summary of processing personality adaptation evidence and possible trait mutation for a cycle.
/// </summary>
public sealed record CharacterPersonalityAdaptationResult(
    Guid EvidenceId,
    Guid CharacterId,
    Guid ExecutionId,
    string TraitKey,
    int Direction,
    int Strength,
    bool AdaptationTriggered,
    int? TraitValueBefore = null,
    int? TraitValueAfter = null,
    int? TraitDelta = null,
    string? AdaptationFingerprint = null
);

/// <summary>
/// Service orchestrating personality adaptation evidence derivation, idempotency checking,
/// persistence, threshold accumulation, and optimistic concurrency mutation.
/// </summary>
public interface IPersonalityAdaptationService
{
    /// <summary>
    /// Processes all personality adaptation proposals derived from the cognitive cycle.
    /// Note: Personality adaptations are independently persisted per trait; partial personality adaptation across traits is allowed.
    /// Failure in one trait adaptation does not invalidate or roll back previously committed adaptations.
    /// </summary>
    Task<IReadOnlyList<CharacterPersonalityAdaptationResult>> ProcessAdaptationsAsync(
        CharacterCognitiveCycleContext context,
        CharacterCognitiveCycleResult result,
        CancellationToken ct = default);

    Task<CharacterPersonalityAdaptationResult?> ProcessAdaptationAsync(
        CharacterCognitiveCycleContext context,
        CharacterCognitiveCycleResult result,
        CancellationToken ct = default);
}
