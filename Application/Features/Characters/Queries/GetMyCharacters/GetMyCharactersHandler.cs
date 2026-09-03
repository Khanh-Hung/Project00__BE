using Application.Abstractions.Auth;
using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.DTOs;
using Domain.Entities;
using MediatR;
using Microsoft.AspNetCore.Http;

namespace Application.Features.Characters.Queries.GetMyCharacters;

public sealed class GetMyCharactersHandler : IRequestHandler<GetMyCharactersQuery, Result<IReadOnlyList<CharacterDto>>>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUserProvider _currentUserProvider;

    public GetMyCharactersHandler(IUnitOfWork unitOfWork, ICurrentUserProvider currentUserProvider)
    {
        _unitOfWork = unitOfWork;
        _currentUserProvider = currentUserProvider;
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

        var userRepo = _unitOfWork.GetRepository<User>();
        User? creator = null;
        if (Guid.TryParse(currentUserId, out var creatorGuid))
        {
            creator = await userRepo.GetByIdAsync(creatorGuid, cancellationToken);
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
                    creator?.DisplayName,
                    creator?.UserName,
                    creator?.AvatarUrl,
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
