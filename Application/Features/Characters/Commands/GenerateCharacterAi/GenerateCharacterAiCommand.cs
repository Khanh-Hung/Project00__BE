using Application.Abstractions.Responses;
using Application.DTOs;
using Application.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Http;

namespace Application.Features.Characters.Commands.GenerateCharacterAI;

public record GenerateCharacterAICommand(GenerateCharacterAIRequest Request) : IRequest<Result<GeneratedCharacterDto>>;

public sealed class GenerateCharacterAIHandler : IRequestHandler<GenerateCharacterAICommand, Result<GeneratedCharacterDto>>
{
    private readonly ILLMService _llmService;

    public GenerateCharacterAIHandler(ILLMService llmService)
    {
        _llmService = llmService;
    }

    public async Task<Result<GeneratedCharacterDto>> Handle(GenerateCharacterAICommand command, CancellationToken cancellationToken)
    {
        var req = command.Request;
        if (string.IsNullOrWhiteSpace(req.Idea))
        {
            return Result<GeneratedCharacterDto>.Failure(StatusCodes.Status400BadRequest, "Character idea cannot be empty.");
        }

        try
        {
            var generated = await _llmService.GenerateCharacterProfileAsync(req.Idea, req.Category, cancellationToken);
            return Result<GeneratedCharacterDto>.Success(generated);
        }
        catch (Exception ex)
        {
            return Result<GeneratedCharacterDto>.Failure(StatusCodes.Status500InternalServerError, ex.Message);
        }
    }
}
