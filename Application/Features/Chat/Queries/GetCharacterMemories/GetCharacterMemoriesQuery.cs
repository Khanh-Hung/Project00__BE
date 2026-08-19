using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.DTOs;
using MediatR;
using Microsoft.AspNetCore.Http;

namespace Application.Features.Chat.Queries.GetCharacterMemories;

public record GetCharacterMemoriesQuery(
    Guid CharacterId,
    Guid? UserId,
    int Limit = 30
) : IRequest<Result<List<CharacterMemoryDto>>>;

public sealed class GetCharacterMemoriesHandler : IRequestHandler<GetCharacterMemoriesQuery, Result<List<CharacterMemoryDto>>>
{
    private readonly IUnitOfWork _unitOfWork;

    public GetCharacterMemoriesHandler(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<List<CharacterMemoryDto>>> Handle(GetCharacterMemoriesQuery query, CancellationToken cancellationToken)
    {
        if (!query.UserId.HasValue || query.UserId.Value == Guid.Empty)
        {
            return Result<List<CharacterMemoryDto>>.Success([]);
        }

        var limit = Math.Clamp(query.Limit, 1, 100);
        var memories = await _unitOfWork.CharacterMemories.GetMostRecentAsync(
            query.UserId.Value,
            query.CharacterId,
            limit,
            cancellationToken);

        var dtos = memories.Select(m => new CharacterMemoryDto(
            m.Id,
            m.Content,
            m.Type,
            m.Importance,
            m.Confidence,
            m.CreatedAt,
            m.LastAccessedAt
        )).ToList();

        return Result<List<CharacterMemoryDto>>.Success(dtos);
    }
}
