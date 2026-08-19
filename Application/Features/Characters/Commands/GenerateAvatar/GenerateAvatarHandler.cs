using Application.Abstractions.Responses;
using Application.DTOs;
using Application.Interfaces;
using MediatR;

namespace Application.Features.Characters.Commands.GenerateAvatar;

public sealed class GenerateAvatarHandler : IRequestHandler<GenerateAvatarCommand, Result<GenerateAvatarResponse>>
{
    private readonly ILLMService _llmService;

    public GenerateAvatarHandler(ILLMService llmService)
    {
        _llmService = llmService;
    }

    public async Task<Result<GenerateAvatarResponse>> Handle(GenerateAvatarCommand command, CancellationToken cancellationToken)
    {
        var response = await _llmService.GenerateAvatarAsync(command.Request, cancellationToken);
        return Result<GenerateAvatarResponse>.Success(response);
    }
}
