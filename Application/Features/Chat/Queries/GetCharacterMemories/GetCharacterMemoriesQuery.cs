using Application.Abstractions.Auth;
using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.DTOs;
using MediatR;

namespace Application.Features.Chat.Queries.GetCharacterMemories;

public record GetCharacterMemoriesQuery(
    Guid CharacterId,
    Guid? UserId = null,
    int Limit = 30
) : IRequest<Result<List<CharacterMemoryDto>>>;

public sealed class GetCharacterMemoriesHandler : IRequestHandler<GetCharacterMemoriesQuery, Result<List<CharacterMemoryDto>>>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUserProvider _currentUserProvider;

    public GetCharacterMemoriesHandler(IUnitOfWork unitOfWork, ICurrentUserProvider currentUserProvider)
    {
        _unitOfWork = unitOfWork;
        _currentUserProvider = currentUserProvider;
    }

    public async Task<Result<List<CharacterMemoryDto>>> Handle(GetCharacterMemoriesQuery query, CancellationToken cancellationToken)
    {
        Guid? effectiveUserId = query.UserId;
        if (!effectiveUserId.HasValue || effectiveUserId.Value == Guid.Empty)
        {
            if (!string.IsNullOrEmpty(_currentUserProvider.CurrentUserId) && Guid.TryParse(_currentUserProvider.CurrentUserId, out var uid))
            {
                effectiveUserId = uid;
            }
        }

        if (!effectiveUserId.HasValue || effectiveUserId.Value == Guid.Empty)
        {
            return Result<List<CharacterMemoryDto>>.Success([]);
        }

        var limit = Math.Clamp(query.Limit, 1, 100);
        var memories = await _unitOfWork.CharacterMemories.GetMostRecentAsync(
            effectiveUserId.Value,
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
