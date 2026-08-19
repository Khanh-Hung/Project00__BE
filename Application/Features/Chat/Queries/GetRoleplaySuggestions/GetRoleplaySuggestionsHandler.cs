using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.Interfaces;
using Domain.Entities;
using MediatR;
using Microsoft.AspNetCore.Http;

namespace Application.Features.Chat.Queries.GetRoleplaySuggestions;

public sealed class GetRoleplaySuggestionsHandler : IRequestHandler<GetRoleplaySuggestionsQuery, Result<List<string>>>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILLMService _llmService;

    public GetRoleplaySuggestionsHandler(IUnitOfWork unitOfWork, ILLMService llmService)
    {
        _unitOfWork = unitOfWork;
        _llmService = llmService;
    }

    public async Task<Result<List<string>>> Handle(GetRoleplaySuggestionsQuery request, CancellationToken cancellationToken)
    {
        var sessionRepo = _unitOfWork.GetRepository<ChatSession>();
        var characterRepo = _unitOfWork.GetRepository<Character>();

        var session = await sessionRepo.GetByIdAsync(request.SessionId, cancellationToken);
        if (session == null)
        {
            return Result<List<string>>.Failure(StatusCodes.Status404NotFound, $"Chat session with ID '{request.SessionId}' was not found.");
        }

        var character = await characterRepo.GetByIdAsync(session.CharacterId, cancellationToken);
        if (character == null)
        {
            return Result<List<string>>.Failure(StatusCodes.Status404NotFound, "Character for this session was not found.");
        }

        var suggestions = await _llmService.GenerateRoleplaySuggestionsAsync(
            character,
            session.Messages,
            cancellationToken);

        return Result<List<string>>.Success(suggestions);
    }
}
