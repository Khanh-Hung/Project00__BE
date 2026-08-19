using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.DTOs;
using Domain.Entities;
using Infrastructure.LLM.Prompts;
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

        // Account-based privacy filtering: only show sessions belonging to the current user
        var filteredSessions = query.UserId.HasValue
            ? sessions.Where(s => s.UserId == query.UserId.Value)
            : sessions.Where(s => s.UserId == null);

        var userRelationships = new Dictionary<Guid, CharacterRelationship>();
        if (query.UserId.HasValue && query.UserId.Value != Guid.Empty)
        {
            var rels = await _unitOfWork.GetRepository<CharacterRelationship>().GetAllAsync(ct: cancellationToken);
            userRelationships = rels
                .Where(r => r.UserId == query.UserId.Value)
                .ToDictionary(r => r.CharacterId);
        }

        var list = filteredSessions
            .Where(s => charDict.ContainsKey(s.CharacterId))
            .OrderByDescending(s => s.Messages.LastOrDefault()?.CreatedAt ?? s.CreatedAt)
            .Select(s =>
            {
                var character = charDict[s.CharacterId];
                var lastMsg = s.Messages.LastOrDefault();

                userRelationships.TryGetValue(s.CharacterId, out var relationship);
                var affection = relationship?.AffectionScore ?? character.DefaultAffectionScore;
                var (level, stageName, _) = Application.Common.RelationshipStageResolver.Resolve(affection, character.CustomMilestonesJson);

                return new ChatSessionListItemDto(
                    s.Id,
                    s.CharacterId,
                    character.Name,
                    character.AvatarUrl,
                    s.Title,
                    lastMsg?.Content,
                    lastMsg?.CreatedAt,
                    s.Messages.Count,
                    s.CreatedAt,
                    affection,
                    level,
                    stageName
                );
            })
            .ToList();

        return Result<IReadOnlyList<ChatSessionListItemDto>>.Success(list);
    }
}
