using Application.Abstractions.Auth;
using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.DTOs;
using Domain.Entities;
using MediatR;
using Microsoft.AspNetCore.Http;

namespace Application.Features.Characters.Queries.GetCharacterById;

public sealed class GetCharacterByIdHandler : IRequestHandler<GetCharacterByIdQuery, Result<CharacterDto>>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IIdentityUnitOfWork _identityUnitOfWork;
    private readonly ICurrentUserProvider _currentUserProvider;

    public GetCharacterByIdHandler(
        IUnitOfWork unitOfWork,
        IIdentityUnitOfWork identityUnitOfWork,
        ICurrentUserProvider currentUserProvider)
    {
        _unitOfWork = unitOfWork;
        _identityUnitOfWork = identityUnitOfWork;
        _currentUserProvider = currentUserProvider;
    }

    public GetCharacterByIdHandler(IUnitOfWork unitOfWork, ICurrentUserProvider currentUserProvider)
        : this(unitOfWork, null!, currentUserProvider)
    {
    }

    public async Task<Result<CharacterDto>> Handle(GetCharacterByIdQuery query, CancellationToken cancellationToken)
    {
        var repo = _unitOfWork.GetRepository<Character>();
        var character = await repo.GetByIdAsync(query.Id, cancellationToken);
        if (character == null)
        {
            return Result<CharacterDto>.Failure(StatusCodes.Status404NotFound, $"Character with ID '{query.Id}' was not found.");
        }

        // Privacy check for non-public characters
        var currentUserId = _currentUserProvider.CurrentUserId;
        if (!character.IsPublic)
        {
            if (string.IsNullOrEmpty(currentUserId) ||
                (!string.IsNullOrEmpty(character.CreatedBy) &&
                 character.CreatedBy != "system" &&
                 !string.Equals(character.CreatedBy, currentUserId, StringComparison.OrdinalIgnoreCase)))
            {
                return Result<CharacterDto>.Failure(StatusCodes.Status404NotFound, $"Character with ID '{query.Id}' was not found.");
            }
        }

        User? creator = null;
        if (!string.IsNullOrEmpty(character.CreatedBy) && _identityUnitOfWork != null)
        {
            var userRepo = _identityUnitOfWork.GetRepository<User>();
            if (Guid.TryParse(character.CreatedBy, out var creatorGuid))
            {
                creator = await userRepo.GetByIdAsync(creatorGuid, cancellationToken);
            }
        }

        var customMilestones = !string.IsNullOrWhiteSpace(character.CustomMilestonesJson)
            ? System.Text.Json.JsonSerializer.Deserialize<List<RelationshipMilestoneDto>>(character.CustomMilestonesJson)
            : null;

        var dto = new CharacterDto(
            character.Id,
            character.Name,
            character.Title,
            character.AvatarUrl,
            character.PersonalityPrompt,
            character.Greeting,
            character.Category,
            character.Tags,
            character.IsPublic,
            character.CreatedAt,
            character.CreatedBy,
            CreatorName: creator?.DisplayName ?? (character.CreatedBy == "system" ? "System" : null),
            CreatorUserName: creator?.UserName,
            CreatorAvatar: creator?.AvatarUrl,
            character.DefaultAffectionScore,
            character.DefaultMood,
            customMilestones,
            character.Blueprint,
            character.VisualIdentity,
            character.VoiceProfile,
            character.WorldName,
            character.WorldDescription,
            character.WorldGenre,
            character.CustomPhysicsRules
        );

        return Result<CharacterDto>.Success(dto);
    }
}
