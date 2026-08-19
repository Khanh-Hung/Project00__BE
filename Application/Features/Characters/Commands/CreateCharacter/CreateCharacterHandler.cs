using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.DTOs;
using Domain.Entities;
using MediatR;
using Microsoft.AspNetCore.Http;

namespace Application.Features.Characters.Commands.CreateCharacter;

public sealed class CreateCharacterHandler : IRequestHandler<CreateCharacterCommand, Result<CharacterDto>>
{
    private readonly IUnitOfWork _unitOfWork;

    public CreateCharacterHandler(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<CharacterDto>> Handle(CreateCharacterCommand command, CancellationToken cancellationToken)
    {
        var req = command.Request;
        if (string.IsNullOrWhiteSpace(req.Name))
        {
            return Result<CharacterDto>.Failure(StatusCodes.Status400BadRequest, "Character name is required.");
        }

        var repo = _unitOfWork.GetRepository<Character>();
        var milestonesJson = req.CustomMilestones != null && req.CustomMilestones.Count > 0
            ? System.Text.Json.JsonSerializer.Serialize(req.CustomMilestones)
            : null;

        var character = new Character(
            req.Name,
            req.Title,
            req.AvatarUrl,
            req.PersonalityPrompt,
            req.Greeting,
            req.Category,
            req.Tags,
            req.IsPublic,
            req.DefaultAffectionScore,
            req.DefaultMood,
            milestonesJson
        );

        await repo.AddAsync(character, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

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
            req.CustomMilestones
        );

        return Result<CharacterDto>.Success(dto);
    }
}
