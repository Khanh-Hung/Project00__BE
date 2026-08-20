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

    public GetCharacterByIdHandler(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<CharacterDto>> Handle(GetCharacterByIdQuery query, CancellationToken cancellationToken)
    {
        var repo = _unitOfWork.GetRepository<Character>();
        var character = await repo.GetByIdAsync(query.Id, cancellationToken);
        if (character == null)
        {
            return Result<CharacterDto>.Failure(StatusCodes.Status404NotFound, $"Character with ID '{query.Id}' was not found.");
        }

        User? creator = null;
        if (!string.IsNullOrEmpty(character.CreatedBy))
        {
            var userRepo = _unitOfWork.GetRepository<User>();
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
            creator?.DisplayName ?? (character.CreatedBy == "system" ? "System" : null),
            creator?.UserName,
            creator?.AvatarUrl,
            character.DefaultAffectionScore,
            character.DefaultMood,
            customMilestones,
            character.Blueprint,
            character.WorldName,
            character.WorldDescription,
            character.WorldGenre,
            character.CustomPhysicsRules
        );

        return Result<CharacterDto>.Success(dto);
    }
}
