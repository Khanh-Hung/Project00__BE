using Application.Abstractions.Responses;
using Application.DTOs;
using Application.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Http;

namespace Application.Features.Chat.Commands.ImagineScene;

public record ImagineSceneCommand(GenerateSceneImageRequest Request) : IRequest<Result<GenerateAvatarResponse>>;

public sealed class ImagineSceneHandler : IRequestHandler<ImagineSceneCommand, Result<GenerateAvatarResponse>>
{
    private readonly ILLMService _llmService;

    public ImagineSceneHandler(ILLMService llmService)
    {
        _llmService = llmService;
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
            var res = await _llmService.GenerateSceneImageAsync(req, cancellationToken);
            return Result<GenerateAvatarResponse>.Success(res);
        }
        catch (Exception ex)
        {
            return Result<GenerateAvatarResponse>.Failure(StatusCodes.Status500InternalServerError, ex.Message);
        }
    }
}
