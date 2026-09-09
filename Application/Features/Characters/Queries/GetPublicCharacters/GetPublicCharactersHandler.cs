using Application.Abstractions.Auth;
using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.DTOs;
using Application.Interfaces;
using Domain.Entities;
using MediatR;

namespace Application.Features.Characters.Queries.GetPublicCharacters;

public sealed class GetPublicCharactersHandler : IRequestHandler<GetPublicCharactersQuery, Result<IReadOnlyList<CharacterDto>>>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAccountServiceClient _accountServiceClient;
    private readonly ICurrentUserProvider _currentUserProvider;

    public GetPublicCharactersHandler(
        IUnitOfWork unitOfWork,
        IAccountServiceClient accountServiceClient,
        ICurrentUserProvider currentUserProvider)
    {
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _accountServiceClient = accountServiceClient ?? throw new ArgumentNullException(nameof(accountServiceClient));
        _currentUserProvider = currentUserProvider ?? throw new ArgumentNullException(nameof(currentUserProvider));
    }

    public async Task<Result<IReadOnlyList<CharacterDto>>> Handle(GetPublicCharactersQuery query, CancellationToken cancellationToken)
    {
        var currentUserId = _currentUserProvider.CurrentUserId;
        var repo = _unitOfWork.GetRepository<Character>();
        var characters = await repo.GetAllAsync(
            c => (c.IsPublic || (!string.IsNullOrEmpty(currentUserId) && c.CreatedBy == currentUserId))
                 && (string.IsNullOrWhiteSpace(query.Category) || c.Category.ToLower() == query.Category.ToLower()),
            cancellationToken);

        var creatorGuids = characters
            .Where(c => !string.IsNullOrEmpty(c.CreatedBy) && c.CreatedBy != "system")
            .Select(c => Guid.TryParse(c.CreatedBy, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .Distinct()
            .ToList();

        IReadOnlyDictionary<Guid, AccountUserDto> userMap = new Dictionary<Guid, AccountUserDto>();
        if (creatorGuids.Count > 0)
        {
            userMap = await _accountServiceClient.GetUsersAsync(creatorGuids, cancellationToken);
        }

        var dtos = characters.Select(c =>
        {
            AccountUserDto? creator = null;
            if (!string.IsNullOrEmpty(c.CreatedBy) && Guid.TryParse(c.CreatedBy, out var creatorGuid))
            {
                userMap.TryGetValue(creatorGuid, out creator);
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
                CreatorName: creator?.DisplayName ?? (c.CreatedBy == "system" ? "System" : null),
                CreatorUserName: creator?.UserName,
                CreatorAvatar: creator?.AvatarUrl,
                c.DefaultAffectionScore,
                c.DefaultMood,
                customMilestones,
                c.Blueprint,
                c.VisualIdentity,
                c.VoiceProfile,
                c.WorldName,
                c.WorldDescription,
                c.WorldGenre,
                c.CustomPhysicsRules
            );
        }).ToList();

        return Result<IReadOnlyList<CharacterDto>>.Success(dtos);
    }
}
