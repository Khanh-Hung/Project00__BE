using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.DTOs;
using Domain.Entities;
using MediatR;
using Microsoft.AspNetCore.Http;

namespace Application.Features.Characters.Commands.UpdateCharacter;

public sealed class UpdateCharacterHandler : IRequestHandler<UpdateCharacterCommand, Result<CharacterDto>>
{
    private readonly IUnitOfWork _unitOfWork;

    public UpdateCharacterHandler(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<CharacterDto>> Handle(UpdateCharacterCommand command, CancellationToken cancellationToken)
    {
        var repo = _unitOfWork.GetRepository<Character>();
        var character = await repo.GetByIdAsync(command.Id, cancellationToken);
        if (character == null)
        {
            return Result<CharacterDto>.Failure(StatusCodes.Status404NotFound, $"Character with ID '{command.Id}' was not found.");
        }

        var req = command.Request;
        var milestonesJson = req.CustomMilestones != null
            ? System.Text.Json.JsonSerializer.Serialize(req.CustomMilestones)
            : null;

        character.UpdateDetails(
            req.Name,
            req.Title,
            req.AvatarUrl,
            req.PersonalityPrompt,
            req.Greeting,
            req.Category,
            req.Tags ?? [],
            req.DefaultAffectionScore,
            req.DefaultMood,
            milestonesJson,
            req.Blueprint,
            updateBlueprint: req.Blueprint != null,
            worldName: req.WorldName,
            worldDescription: req.WorldDescription
        );
        character.SetPublicStatus(req.IsPublic);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

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
            null,
            null,
            null,
            character.DefaultAffectionScore,
            character.DefaultMood,
            customMilestones,
            character.Blueprint,
            character.WorldName,
            character.WorldDescription
        );

        return Result<CharacterDto>.Success(dto);
    }
}
