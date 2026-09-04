using System;
using System.Threading;
using System.Threading.Tasks;
using Application.Abstractions.Data;
using Application.Contracts.CognitiveCycle;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services.CognitiveCycle;

/// <summary>
/// Infrastructure service for retrieving contextual relationship data for the cognitive cycle.
/// Invariant: Retrieval is authoritative; missing relationships are created deterministically (Stranger, 0, 0, 0).
/// Invariant: Catches and logs unexpected retrieval failures for graceful degradation.
/// </summary>
public sealed class CharacterRelationshipRetrievalService : ICharacterRelationshipRetrievalService
{
    private readonly ICharacterRelationshipRepository _relationshipRepository;
    private readonly ILogger<CharacterRelationshipRetrievalService> _logger;

    public CharacterRelationshipRetrievalService(
        ICharacterRelationshipRepository relationshipRepository,
        ILogger<CharacterRelationshipRetrievalService> logger)
    {
        _relationshipRepository = relationshipRepository ?? throw new ArgumentNullException(nameof(relationshipRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CharacterRelationshipContext?> RetrieveRelationshipAsync(
        Guid characterId,
        CharacterCognitiveEvent? cognitiveEvent,
        CancellationToken ct = default)
    {
        if (characterId == Guid.Empty)
        {
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));
        }

        // Only retrieve relationship if event identifies an explicit relationship target
        var targetInfo = cognitiveEvent?.Target;
        if (!targetInfo.HasValue)
        {
            return null;
        }

        var (targetType, targetId) = targetInfo.Value;

        try
        {
            var relationship = await _relationshipRepository.GetOrCreateByTargetAsync(
                characterId,
                targetType,
                targetId,
                ct);

            return new CharacterRelationshipContext(
                TargetId: relationship.TargetId,
                TargetType: relationship.TargetType,
                RelationshipType: relationship.RelationshipType,
                Trust: relationship.Trust,
                Affection: relationship.Affection,
                Familiarity: relationship.Familiarity
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[CharacterRelationshipRetrievalService] Failed to retrieve relationship for CharacterId={CharacterId}, TargetType={TargetType}, TargetId={TargetId}. Gracefully falling back to null.",
                characterId, targetType, targetId);

            return null;
        }
    }
}
