using Application.Abstractions.Responses;
using Application.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Http;

namespace Application.Features.Characters.Queries.GenerateRandomIdeas;

public record GenerateRandomIdeasQuery(int Count = 3) : IRequest<Result<List<string>>>;

public sealed class GenerateRandomIdeasHandler : IRequestHandler<GenerateRandomIdeasQuery, Result<List<string>>>
{
    private readonly ILLMService _llmService;

    public GenerateRandomIdeasHandler(ILLMService llmService)
    {
        _llmService = llmService;
    }

    public async Task<Result<List<string>>> Handle(GenerateRandomIdeasQuery query, CancellationToken cancellationToken)
    {
        try
        {
            var count = Math.Clamp(query.Count, 1, 10);
            var ideas = await _llmService.GenerateRandomIdeasAsync(count, cancellationToken);
            return Result<List<string>>.Success(ideas);
        }
        catch (Exception ex)
        {
            return Result<List<string>>.Failure(StatusCodes.Status500InternalServerError, ex.Message);
        }
    }
}
