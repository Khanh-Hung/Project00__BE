using System;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;
using Domain.Enums;

namespace Application.Abstractions.Data;

public interface ICharacterRelationshipRepository
{
    Task<CharacterRelationship?> GetByPairAsync(Guid userId, Guid characterId, CancellationToken ct = default);
    Task<CharacterRelationship> GetOrCreateAsync(
        Guid userId,
        Guid characterId,
        int initialAffection = 0,
        CharacterMood initialMood = CharacterMood.Neutral,
        CancellationToken ct = default);

    Task<CharacterRelationship?> GetByTargetAsync(
        Guid characterId,
        RelationshipTargetType targetType,
        Guid targetId,
        CancellationToken ct = default);

    Task<CharacterRelationship> GetOrCreateByTargetAsync(
        Guid characterId,
        RelationshipTargetType targetType,
        Guid targetId,
        CancellationToken ct = default);

    Task AddAsync(CharacterRelationship relationship, CancellationToken ct = default);
}
