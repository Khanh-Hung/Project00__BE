using System;
using System.Threading;
using System.Threading.Tasks;
using Domain.ValueObjects;

namespace Application.Contracts.CognitiveCycle;

/// <summary>
/// Service responsible for authoritative retrieval of the contextual relationship between a character
/// and an identified target entity (such as user from incoming message).
/// Gracefully resolves missing relationships to default initial state (Stranger, Trust=0, Affection=0, Familiarity=0).
/// </summary>
public interface ICharacterRelationshipRetrievalService
{
    Task<CharacterRelationshipContext?> RetrieveRelationshipAsync(
        Guid characterId,
        CharacterCognitiveEvent? cognitiveEvent,
        CancellationToken ct = default);
}
