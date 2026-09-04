using System;
using System.Threading;
using System.Threading.Tasks;
using Domain.ValueObjects;

namespace Application.Contracts.CognitiveCycle;

/// <summary>
/// Application boundary service for retrieving relevant memories for cognitive perception.
/// Failure must degrade gracefully to empty context without failing cognition.
/// </summary>
public interface ICharacterMemoryRetrievalService
{
    Task<CharacterMemoryContext> RetrieveRelevantAsync(
        Guid characterId,
        CharacterPerceptionContext perceptionContext,
        CancellationToken ct = default);
}
