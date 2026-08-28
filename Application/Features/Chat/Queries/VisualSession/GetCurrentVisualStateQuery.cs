using Application.Abstractions.Auth;
using Application.Abstractions.Responses;
using Application.DTOs;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Application.Features.Chat.Queries.VisualSession;

public sealed record GetCurrentVisualStateQuery(Guid SessionId) : IRequest<Result<VisualArtifactResponse>>;

public sealed class GetCurrentVisualStateHandler : IRequestHandler<GetCurrentVisualStateQuery, Result<VisualArtifactResponse>>
{
    private readonly CoreDbContext _dbContext;
    private readonly ICurrentUserProvider _currentUserProvider;
    private readonly ILogger<GetCurrentVisualStateHandler> _logger;

    public GetCurrentVisualStateHandler(
        CoreDbContext dbContext,
        ICurrentUserProvider currentUserProvider,
        ILogger<GetCurrentVisualStateHandler> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _currentUserProvider = currentUserProvider ?? throw new ArgumentNullException(nameof(currentUserProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<Result<VisualArtifactResponse>> Handle(GetCurrentVisualStateQuery request, CancellationToken cancellationToken)
    {
        // 1. Resolve Authenticated User & Check Ownership
        if (string.IsNullOrEmpty(_currentUserProvider.CurrentUserId) || !Guid.TryParse(_currentUserProvider.CurrentUserId, out var currentUserId))
        {
            return Result<VisualArtifactResponse>.Failure(
                StatusCodes.Status401Unauthorized,
                "Authentication is required to view visual session state.");
        }

        var session = await _dbContext.ChatSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == request.SessionId, cancellationToken);

        if (session == null)
        {
            return Result<VisualArtifactResponse>.Failure(
                StatusCodes.Status404NotFound,
                $"Chat session '{request.SessionId}' was not found.");
        }

        if (session.UserId != currentUserId)
        {
            return Result<VisualArtifactResponse>.Failure(
                StatusCodes.Status403Forbidden,
                "You do not have permission to access visual state for this session.");
        }

        // 2. Authoritative VisualSessionState Lookup (Strict: no silent fallbacks that hide state divergence)
        var sessionState = await _dbContext.VisualSessionStates
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.SessionId == request.SessionId, cancellationToken);

        if (sessionState == null || !sessionState.CurrentImageId.HasValue)
        {
            return Result<VisualArtifactResponse>.Failure(
                StatusCodes.Status404NotFound,
                $"No active visual artifact found for session '{request.SessionId}'.");
        }

        var currentArtifact = await _dbContext.SceneImages
            .AsNoTracking()
            .FirstOrDefaultAsync(img => img.Id == sessionState.CurrentImageId.Value
                                        && img.SessionId == request.SessionId, cancellationToken);

        if (currentArtifact == null)
        {
            _logger.LogError("[GetCurrentVisualState] State divergence detected: SessionState for {SessionId} references CurrentImageId {ArtifactId} which does not exist in SceneImages.",
                request.SessionId, sessionState.CurrentImageId.Value);

            return Result<VisualArtifactResponse>.Failure(
                StatusCodes.Status404NotFound,
                $"Active visual artifact '{sessionState.CurrentImageId.Value}' was not found.");
        }

        if (currentArtifact.LifecycleStatus == ArtifactLifecycleStatus.Quarantined
            || currentArtifact.LifecycleStatus == ArtifactLifecycleStatus.Deleted)
        {
            _logger.LogError("[GetCurrentVisualState] State divergence detected: SessionState for {SessionId} references CurrentImageId {ArtifactId} with invalid status {Status}.",
                request.SessionId, currentArtifact.Id, currentArtifact.LifecycleStatus);

            return Result<VisualArtifactResponse>.Failure(
                StatusCodes.Status404NotFound,
                $"Active visual artifact is in invalid status '{currentArtifact.LifecycleStatus}'.");
        }

        var response = new VisualArtifactResponse(
            ArtifactId: currentArtifact.Id,
            TurnId: currentArtifact.TurnId,
            SessionId: currentArtifact.SessionId,
            ImageUrl: currentArtifact.ImageUrl,
            IsCurrent: currentArtifact.IsCurrent && currentArtifact.LifecycleStatus == ArtifactLifecycleStatus.Current,
            VisualRevision: sessionState.VisualRevision,
            SceneRevision: currentArtifact.SceneRevision,
            CreatedAt: currentArtifact.CreatedAt,
            Prompt: currentArtifact.Prompt,
            Model: currentArtifact.GetProvenance()?.ModelIdentifier,
            LifecycleStatus: currentArtifact.LifecycleStatus.ToString(),
            PredecessorArtifactId: currentArtifact.PredecessorArtifactId
        );

        return Result<VisualArtifactResponse>.Success(response);
    }
}
