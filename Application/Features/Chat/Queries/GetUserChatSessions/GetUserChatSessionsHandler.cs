using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.DTOs;
using Domain.Entities;
using MediatR;

namespace Application.Features.Chat.Queries.GetUserChatSessions;

public sealed class GetUserChatSessionsHandler : IRequestHandler<GetUserChatSessionsQuery, Result<IReadOnlyList<ChatSessionListItemDto>>>
{
    private readonly IUnitOfWork _unitOfWork;

    public GetUserChatSessionsHandler(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<IReadOnlyList<ChatSessionListItemDto>>> Handle(GetUserChatSessionsQuery query, CancellationToken cancellationToken)
    {
        var sessionRepo = _unitOfWork.GetRepository<ChatSession>();
        var characterRepo = _unitOfWork.GetRepository<Character>();

        var sessions = await sessionRepo.GetAllAsync(ct: cancellationToken);
        var characters = await characterRepo.GetAllAsync(ct: cancellationToken);
        var charDict = characters.ToDictionary(c => c.Id);

        var list = sessions
            .Where(s => charDict.ContainsKey(s.CharacterId))
            .OrderByDescending(s => s.Messages.LastOrDefault()?.CreatedAt ?? s.CreatedAt)
            .Select(s =>
            {
                var character = charDict[s.CharacterId];
                var lastMsg = s.Messages.LastOrDefault();
                return new ChatSessionListItemDto(
                    s.Id,
                    s.CharacterId,
                    character.Name,
                    character.AvatarUrl,
                    s.Title,
                    lastMsg?.Content,
                    lastMsg?.CreatedAt,
                    s.Messages.Count,
                    s.CreatedAt
                );
            })
            .ToList();

        return Result<IReadOnlyList<ChatSessionListItemDto>>.Success(list);
    }
}
