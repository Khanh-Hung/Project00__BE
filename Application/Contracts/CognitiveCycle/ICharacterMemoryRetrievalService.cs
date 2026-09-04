using System;
using System.Threading;
using System.Threading.Tasks;
using Domain.ValueObjects;

namespace Application.Contracts.CognitiveCycle;

/// <summary>
/// Application boundary service for contextual memory retrieval for a character's cognitive cycle.
/// In PR47 MVP, this implements deterministic character-scoped contextual memory retrieval (Importance DESC, CreatedAt DESC, Id ASC).
/// Semantic relevance based on perceptionContext (e.g., embeddings/vector search) is intentionally deferred to a later Memory Retrieval enhancement.
/// Failure must degrade gracefully to empty context without failing cognition.
/// </summary>
public interface ICharacterMemoryRetrievalService
{
    /// <summary>
    /// Retrieves contextual memories for the character.
    /// In PR47 MVP, this retrieves the most important and recent memories deterministically.
    /// </summary>
    Task<CharacterMemoryContext> RetrieveRelevantAsync(
        Guid characterId,
        CharacterPerceptionContext perceptionContext,
        CancellationToken ct = default);
}
