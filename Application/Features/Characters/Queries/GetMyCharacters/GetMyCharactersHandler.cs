using Application.Abstractions.Auth;
using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.DTOs;
using Application.Interfaces;
using Domain.Entities;
using MediatR;
using Microsoft.AspNetCore.Http;

namespace Application.Features.Characters.Queries.GetMyCharacters;

public sealed class GetMyCharactersHandler : IRequestHandler<GetMyCharactersQuery, Result<IReadOnlyList<CharacterDto>>>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAccountServiceClient? _accountServiceClient;
    private readonly ICurrentUserProvider _currentUserProvider;

    public GetMyCharactersHandler(
        IUnitOfWork unitOfWork,
        IAccountServiceClient? accountServiceClient,
        ICurrentUserProvider currentUserProvider)
    {
        _unitOfWork = unitOfWork;
        _accountServiceClient = accountServiceClient;
        _currentUserProvider = currentUserProvider;
    }

    public GetMyCharactersHandler(IUnitOfWork unitOfWork, ICurrentUserProvider currentUserProvider)
        : this(unitOfWork, null, currentUserProvider)
    {
    }

    public async Task<Result<IReadOnlyList<CharacterDto>>> Handle(GetMyCharactersQuery query, CancellationToken cancellationToken)
    {
        var currentUserId = _currentUserProvider.CurrentUserId;
        if (string.IsNullOrWhiteSpace(currentUserId))
        {
            return Result<IReadOnlyList<CharacterDto>>.Failure(StatusCodes.Status401Unauthorized, "User is not authenticated.");
        }

        var repo = _unitOfWork.GetRepository<Character>();
        var characters = await repo.GetAllAsync(
            c => c.CreatedBy == currentUserId,
            cancellationToken);

        AccountUserDto? creator = null;
        if (Guid.TryParse(currentUserId, out var creatorGuid) && _accountServiceClient != null)
        {
            creator = await _accountServiceClient.GetUserAsync(creatorGuid, cancellationToken);
        }

        var dtos = characters
            .OrderByDescending(c => c.CreatedAt)
            .Select(c =>
            {
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
                    CreatorName: creator?.DisplayName,
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
