using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.DTOs;
using Application.Interfaces;
using Domain.Entities;
using MediatR;
using Microsoft.AspNetCore.Http;

namespace Application.Features.Chat.Commands.ImagineScene;

public record ImagineSceneCommand(GenerateSceneImageRequest Request) : IRequest<Result<GenerateAvatarResponse>>;

public sealed class ImagineSceneHandler : IRequestHandler<ImagineSceneCommand, Result<GenerateAvatarResponse>>
{
    private readonly ILLMService _llmService;
    private readonly IUnitOfWork _uow;

    public ImagineSceneHandler(ILLMService llmService, IUnitOfWork uow)
    {
        _llmService = llmService;
        _uow = uow;
    }

    public async Task<Result<GenerateAvatarResponse>> Handle(ImagineSceneCommand command, CancellationToken cancellationToken)
    {
        var req = command.Request;
        if (string.IsNullOrWhiteSpace(req.MessageContent))
        {
            return Result<GenerateAvatarResponse>.Failure(StatusCodes.Status400BadRequest, "Message content cannot be empty.");
        }

        try
        {
            if (req.SessionId.HasValue)
            {
                var session = await _uow.GetRepository<ChatSession>().GetByIdAsync(req.SessionId.Value, cancellationToken);
                if (session != null)
                {
                    var character = await _uow.GetRepository<Character>().GetByIdAsync(session.CharacterId, cancellationToken);
                    if (character != null)
                    {
                        req = req with
                        {
                            CharacterName = string.IsNullOrWhiteSpace(req.CharacterName) ? character.Name : req.CharacterName,
                            CharacterTitle = string.IsNullOrWhiteSpace(req.CharacterTitle) ? character.Title : req.CharacterTitle,
                            CharacterPersonality = string.IsNullOrWhiteSpace(req.CharacterPersonality) ? character.PersonalityPrompt : req.CharacterPersonality,
                            VisualIdentity = character.VisualIdentity,
                            WorldDescription = character.WorldDescription,
                            ReferenceImageUrl = character.VisualIdentity?.CanonicalReferenceUrl 
                                ?? character.VisualIdentity?.FullBodyUrl 
                                ?? (!string.IsNullOrWhiteSpace(character.AvatarUrl) ? character.AvatarUrl : req.ReferenceImageUrl),
                            SceneState = session.SceneState
                        };
                    }
                }
            }

            var res = await _llmService.GenerateSceneImageAsync(req, cancellationToken);
            return Result<GenerateAvatarResponse>.Success(res);
        }
        catch (Exception ex)
        {
            return Result<GenerateAvatarResponse>.Failure(StatusCodes.Status500InternalServerError, ex.Message);
        }
    }
}
