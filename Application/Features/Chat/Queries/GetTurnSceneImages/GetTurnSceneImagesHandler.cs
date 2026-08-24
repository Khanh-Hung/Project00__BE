using Application.Abstractions.Auth;
using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.DTOs;
using Domain.Entities;
using MediatR;
using Microsoft.AspNetCore.Http;

namespace Application.Features.Chat.Queries.GetTurnSceneImages;

public sealed class GetTurnSceneImagesHandler : IRequestHandler<GetTurnSceneImagesQuery, Result<List<SceneImageDto>>>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUserProvider _currentUserProvider;

    public GetTurnSceneImagesHandler(
        IUnitOfWork unitOfWork,
        ICurrentUserProvider currentUserProvider)
    {
        _unitOfWork = unitOfWork;
        _currentUserProvider = currentUserProvider;
    }

    public async Task<Result<List<SceneImageDto>>> Handle(GetTurnSceneImagesQuery query, CancellationToken cancellationToken)
    {
        // 1. Resolve Authenticated User
        if (string.IsNullOrEmpty(_currentUserProvider.CurrentUserId) || !Guid.TryParse(_currentUserProvider.CurrentUserId, out var currentUserId))
        {
            return Result<List<SceneImageDto>>.Failure(
                StatusCodes.Status401Unauthorized,
                "Authentication is required to query scene images.");
        }

        var sessionRepo = _unitOfWork.GetRepository<ChatSession>();
        var sceneImageRepo = _unitOfWork.GetRepository<SceneImage>();

        // 2. Fetch ChatSession & Verify Ownership
        var session = await sessionRepo.GetByIdAsync(query.SessionId, cancellationToken);
        if (session == null)
        {
            return Result<List<SceneImageDto>>.Failure(
                StatusCodes.Status404NotFound,
                $"Chat session '{query.SessionId}' was not found.");
        }

        // Strict Fail-Closed Authorization: Session.UserId MUST exist, not be Guid.Empty, and equal CurrentUserId
        if (!session.UserId.HasValue || session.UserId.Value == Guid.Empty || session.UserId.Value != currentUserId)
        {
            return Result<List<SceneImageDto>>.Failure(
                StatusCodes.Status403Forbidden,
                "You do not have access to this chat session.");
        }

        // 3. Verify Turn exists in this Session and belongs to the authenticated user
        var turnRepo = _unitOfWork.GetRepository<CharacterTurn>();
        var turn = await turnRepo.GetAsync(
            t => t.TurnId == query.TurnId && t.SessionId == query.SessionId,
            cancellationToken);

        if (turn == null)
        {
            return Result<List<SceneImageDto>>.Failure(
                StatusCodes.Status404NotFound,
                $"Turn '{query.TurnId}' in session '{query.SessionId}' was not found.");
        }

        if (turn.UserId != currentUserId)
        {
            return Result<List<SceneImageDto>>.Failure(
                StatusCodes.Status403Forbidden,
                "You do not have access to this turn.");
        }

        // 4. Query all SceneImages for the given Session and Turn
        var images = await sceneImageRepo.GetAllAsync(
            i => i.SessionId == query.SessionId && i.TurnId == query.TurnId,
            cancellationToken);

        var dtos = images
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new SceneImageDto(
                Id: i.Id,
                SessionId: i.SessionId,
                CharacterId: i.CharacterId,
                TurnId: i.TurnId,
                SceneRevision: i.SceneRevision,
                GenerationRequestId: i.GenerationRequestId,
                ImageUrl: i.ImageUrl,
                Prompt: i.Prompt,
                IsCurrent: i.IsCurrent,
                CreatedAt: i.CreatedAt
            ))
            .ToList();

        return Result<List<SceneImageDto>>.Success(dtos);
    }
}
