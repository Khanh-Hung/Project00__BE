using Application.Abstractions.Auth;
using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.DTOs;
using Domain.Entities;
using MediatR;
using Microsoft.AspNetCore.Http;

namespace Application.Features.Chat.Queries.GetCharacterRelationship;

public record GetCharacterRelationshipQuery(
    Guid CharacterId,
    Guid? UserId = null
) : IRequest<Result<CharacterRelationshipDto>>;

public sealed class GetCharacterRelationshipHandler : IRequestHandler<GetCharacterRelationshipQuery, Result<CharacterRelationshipDto>>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUserProvider _currentUserProvider;

    public GetCharacterRelationshipHandler(IUnitOfWork unitOfWork, ICurrentUserProvider currentUserProvider)
    {
        _unitOfWork = unitOfWork;
        _currentUserProvider = currentUserProvider;
    }

    public async Task<Result<CharacterRelationshipDto>> Handle(GetCharacterRelationshipQuery query, CancellationToken cancellationToken)
    {
        Guid? effectiveUserId = query.UserId;
        if (!effectiveUserId.HasValue || effectiveUserId.Value == Guid.Empty)
        {
            if (!string.IsNullOrEmpty(_currentUserProvider.CurrentUserId) && Guid.TryParse(_currentUserProvider.CurrentUserId, out var uid))
            {
                effectiveUserId = uid;
            }
        }

        if (!effectiveUserId.HasValue || effectiveUserId.Value == Guid.Empty)
        {
            return Result<CharacterRelationshipDto>.Failure(StatusCodes.Status401Unauthorized, "User is not authenticated.");
        }

        var character = await _unitOfWork.GetRepository<Character>().GetByIdAsync(query.CharacterId, cancellationToken);
        if (character == null)
        {
            return Result<CharacterRelationshipDto>.Failure(StatusCodes.Status404NotFound, $"Character with ID '{query.CharacterId}' was not found.");
        }

        var relationship = await _unitOfWork.Relationships.GetOrCreateAsync(
            effectiveUserId.Value,
            query.CharacterId,
            character.DefaultAffectionScore,
            ct: cancellationToken);

        var events = relationship.Events.Select(e => new RelationshipEventDto(
            e.EventKey,
            e.Context,
            e.UnlockedAt
        )).ToList();

        var dto = new CharacterRelationshipDto(
            relationship.Id,
            relationship.CharacterId,
            relationship.UserId,
            relationship.AffectionScore,
            relationship.CurrentMood,
            relationship.MoodIntensity,
            events,
            relationship.LastInteractedAt
        );

        return Result<CharacterRelationshipDto>.Success(dto);
    }
}
