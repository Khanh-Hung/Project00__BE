using Application.Abstractions.Auth;
using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.DTOs;
using Domain.Entities;
using MediatR;

namespace Application.Features.Characters.Queries.GetPublicCharacters;

public sealed class GetPublicCharactersHandler : IRequestHandler<GetPublicCharactersQuery, Result<IReadOnlyList<CharacterDto>>>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUserProvider _currentUserProvider;

    public GetPublicCharactersHandler(IUnitOfWork unitOfWork, ICurrentUserProvider currentUserProvider)
    {
        _unitOfWork = unitOfWork;
        _currentUserProvider = currentUserProvider;
    }

    public async Task<Result<IReadOnlyList<CharacterDto>>> Handle(GetPublicCharactersQuery query, CancellationToken cancellationToken)
    {
        var currentUserId = _currentUserProvider.CurrentUserId;
        var repo = _unitOfWork.GetRepository<Character>();
        var characters = await repo.GetAllAsync(
            c => (c.IsPublic || (!string.IsNullOrEmpty(currentUserId) && c.CreatedBy == currentUserId))
                 && (string.IsNullOrWhiteSpace(query.Category) || c.Category.ToLower() == query.Category.ToLower()),
            cancellationToken);

        var userRepo = _unitOfWork.GetRepository<User>();
        var users = await userRepo.GetAllAsync(ct: cancellationToken);
        var userMap = users.ToDictionary(u => u.Id.ToString(), u => u);

        var dtos = characters.Select(c =>
        {
            User? creator = null;
            if (!string.IsNullOrEmpty(c.CreatedBy))
            {
                userMap.TryGetValue(c.CreatedBy, out creator);
            }

            var customMilestones = !string.IsNullOrWhiteSpace(c.CustomMilestonesJson)
                ? System.Text.Json.JsonSerializer.Deserialize<List<RelationshipMilestoneDto>>(c.CustomMilestonesJson)
                : null;

            return new CharacterDto(
                c.Id,
                c.Name,
                c.Title,
                c.AvatarUrl,
                c.PersonalityPrompt,
                c.Greeting,
                c.Category,
                c.Tags,
                c.IsPublic,
                c.CreatedAt,
                c.CreatedBy,
                creator?.DisplayName ?? (c.CreatedBy == "system" ? "System" : null),
                creator?.UserName,
                creator?.AvatarUrl,
                c.DefaultAffectionScore,
                c.DefaultMood,
                customMilestones,
                c.Blueprint,
                c.WorldName,
                c.WorldDescription,
                c.WorldGenre,
                c.CustomPhysicsRules
            );
        }).ToList();

        return Result<IReadOnlyList<CharacterDto>>.Success(dtos);
    }
}
