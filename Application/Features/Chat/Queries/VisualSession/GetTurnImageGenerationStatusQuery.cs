using Application.Abstractions.Auth;
using Application.Abstractions.Responses;
using Application.DTOs;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Application.Features.Chat.Queries.VisualSession;

public sealed record GetTurnImageGenerationStatusQuery(
    Guid SessionId,
    Guid TurnId
) : IRequest<Result<VisualGenerationStatusResponse>>;

public sealed class GetTurnImageGenerationStatusHandler : IRequestHandler<GetTurnImageGenerationStatusQuery, Result<VisualGenerationStatusResponse>>
{
    private readonly ProjectDbContext _dbContext;
    private readonly ICurrentUserProvider _currentUserProvider;

    public GetTurnImageGenerationStatusHandler(
        ProjectDbContext dbContext,
        ICurrentUserProvider currentUserProvider)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _currentUserProvider = currentUserProvider ?? throw new ArgumentNullException(nameof(currentUserProvider));
    }

    public async Task<Result<VisualGenerationStatusResponse>> Handle(GetTurnImageGenerationStatusQuery request, CancellationToken cancellationToken)
    {
        // 1. Resolve Authenticated User & Check Ownership
        if (string.IsNullOrEmpty(_currentUserProvider.CurrentUserId) || !Guid.TryParse(_currentUserProvider.CurrentUserId, out var currentUserId))
        {
            return Result<VisualGenerationStatusResponse>.Failure(
                StatusCodes.Status401Unauthorized,
                "Authentication is required to view turn generation status.");
        }

        var session = await _dbContext.ChatSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == request.SessionId, cancellationToken);

        if (session == null)
        {
            return Result<VisualGenerationStatusResponse>.Failure(
                StatusCodes.Status404NotFound,
                $"Chat session '{request.SessionId}' was not found.");
        }

        if (session.UserId != currentUserId)
        {
            return Result<VisualGenerationStatusResponse>.Failure(
                StatusCodes.Status403Forbidden,
                "You do not have permission to access visual generation status for this session.");
        }

        // 2. Query latest generation job for this turn
        var latestJob = await _dbContext.ImageGenerationJobs
            .AsNoTracking()
            .Where(j => j.SessionId == request.SessionId && j.TurnId == request.TurnId)
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestJob == null)
        {
            return Result<VisualGenerationStatusResponse>.Failure(
                StatusCodes.Status404NotFound,
                $"No generation job found for turn '{request.TurnId}' in session '{request.SessionId}'.");
        }

        // 3. Deterministic Artifact Resolution based on Job Status and Attempt
        SceneImage? artifact = null;

        if (latestJob.AcceptedAttemptId.HasValue || latestJob.Status == ImageJobStatus.Completed)
        {
            artifact = await _dbContext.SceneImages
                .AsNoTracking()
                .Where(img => img.SessionId == request.SessionId
                              && img.GenerationJobId == latestJob.Id
                              && img.LifecycleStatus != ArtifactLifecycleStatus.Deleted)
                .OrderByDescending(img => img.VisualRevision)
                .ThenByDescending(img => img.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
        }
        else if (latestJob.Status == ImageJobStatus.Quarantined)
        {
            artifact = await _dbContext.SceneImages
                .AsNoTracking()
                .Where(img => img.SessionId == request.SessionId
                              && img.GenerationJobId == latestJob.Id
                              && img.LifecycleStatus == ArtifactLifecycleStatus.Quarantined)
                .OrderByDescending(img => img.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
        }
        else
        {
            artifact = await _dbContext.SceneImages
                .AsNoTracking()
                .Where(img => img.SessionId == request.SessionId
                              && (img.GenerationJobId == latestJob.Id || img.GenerationRequestId == latestJob.GenerationRequestId))
                .OrderByDescending(img => img.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
        }

        var sessionState = await _dbContext.VisualSessionStates
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.SessionId == request.SessionId, cancellationToken);

        var response = new VisualGenerationStatusResponse(
            JobId: latestJob.Id,
            GenerationRequestId: latestJob.GenerationRequestId,
            SessionId: latestJob.SessionId,
            TurnId: latestJob.TurnId,
            Status: latestJob.Status.ToString().ToLowerInvariant(),
            AttemptNumber: latestJob.CurrentAttemptNumber,
            HasArtifact: artifact != null,
            ImageUrl: artifact?.ImageUrl,
            FailureReason: latestJob.FailureReason,
            IsQuarantined: latestJob.Status == ImageJobStatus.Quarantined || artifact?.LifecycleStatus == ArtifactLifecycleStatus.Quarantined,
            CompletedAt: latestJob.CompletedAt,
            VisualRevision: artifact?.VisualRevision ?? sessionState?.VisualRevision
        );

        return Result<VisualGenerationStatusResponse>.Success(response);
    }
}
