using Application.Contracts.Reactions;

namespace Application.Interfaces;

/// <summary>
/// Orchestration service for processing world events through perception, reaction, state delta application,
/// goal contribution, memory formation, and visual moment creation.
/// </summary>
public interface ICharacterReactionExecutionService
{
    Task<ReactionExecutionResult> ExecuteReactionAsync(
        ReactionExecutionRequest request,
        CancellationToken ct = default);
}
