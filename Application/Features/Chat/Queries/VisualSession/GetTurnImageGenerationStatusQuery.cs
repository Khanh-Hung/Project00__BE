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
using Microsoft.Extensions.Logging.Abstractions;

namespace Application.Features.Chat.Queries.VisualSession;

public sealed record GetTurnImageGenerationStatusQuery(
    Guid SessionId,
    Guid TurnId
) : IRequest<Result<VisualGenerationStatusResponse>>;

public sealed class GetTurnImageGenerationStatusHandler : IRequestHandler<GetTurnImageGenerationStatusQuery, Result<VisualGenerationStatusResponse>>
{
    private readonly ProjectDbContext _dbContext;
    private readonly ICurrentUserProvider _currentUserProvider;
    private readonly ILogger<GetTurnImageGenerationStatusHandler> _logger;

    public GetTurnImageGenerationStatusHandler(
        ProjectDbContext dbContext,
        ICurrentUserProvider currentUserProvider,
        ILogger<GetTurnImageGenerationStatusHandler>? logger = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _currentUserProvider = currentUserProvider ?? throw new ArgumentNullException(nameof(currentUserProvider));
        _logger = logger ?? NullLogger<GetTurnImageGenerationStatusHandler>.Instance;
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

        // 2. Query generation job for this turn
        var latestJob = await _dbContext.ImageGenerationJobs
            .AsNoTracking()
            .Where(j => j.SessionId == request.SessionId && j.TurnId == request.TurnId)
            .OrderByDescending(j => j.SceneRevision)
            .ThenByDescending(j => j.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestJob == null)
        {
            return Result<VisualGenerationStatusResponse>.Failure(
                StatusCodes.Status404NotFound,
                $"No generation job found for turn '{request.TurnId}' in session '{request.SessionId}'.");
        }

        // 3. Authoritative Direct Foreign Key Traversal: Job.AcceptedAttemptId -> Attempt.AcceptedArtifactId -> SceneImage.Id
        SceneImage? artifact = null;

        if (latestJob.Status == ImageJobStatus.Completed)
        {
            if (!latestJob.AcceptedAttemptId.HasValue)
            {
                _logger.LogError("[GetTurnImageGenerationStatus] State divergence detected: Completed Job {JobId} has null AcceptedAttemptId.",
                    latestJob.Id);

                return Result<VisualGenerationStatusResponse>.Failure(
                    StatusCodes.Status500InternalServerError,
                    $"State divergence: Completed job '{latestJob.Id}' has no recorded AcceptedAttemptId.");
            }

            var winningAttempt = await _dbContext.ImageGenerationAttempts
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == latestJob.AcceptedAttemptId.Value, cancellationToken);

            if (winningAttempt == null)
            {
                _logger.LogError("[GetTurnImageGenerationStatus] State divergence detected: Job {JobId} references AcceptedAttemptId {AttemptId} which does not exist in database.",
                    latestJob.Id, latestJob.AcceptedAttemptId.Value);

                return Result<VisualGenerationStatusResponse>.Failure(
                    StatusCodes.Status500InternalServerError,
                    $"State divergence: Winning attempt '{latestJob.AcceptedAttemptId.Value}' was not found in ledger.");
            }

            if (winningAttempt.Status != GenerationAttemptStatus.Succeeded)
            {
                _logger.LogError("[GetTurnImageGenerationStatus] State divergence detected: Job {JobId} winning attempt {AttemptId} has invalid status {Status}.",
                    latestJob.Id, winningAttempt.Id, winningAttempt.Status);

                return Result<VisualGenerationStatusResponse>.Failure(
                    StatusCodes.Status500InternalServerError,
                    $"State divergence: Winning attempt '{winningAttempt.Id}' is in non-succeeded status '{winningAttempt.Status}'.");
            }

            if (!winningAttempt.AcceptedArtifactId.HasValue)
            {
                _logger.LogError("[GetTurnImageGenerationStatus] State divergence detected: Winning attempt {AttemptId} has null AcceptedArtifactId.",
                    winningAttempt.Id);

                return Result<VisualGenerationStatusResponse>.Failure(
                    StatusCodes.Status500InternalServerError,
                    $"State divergence: Winning attempt '{winningAttempt.Id}' has no attached AcceptedArtifactId.");
            }

            artifact = await _dbContext.SceneImages
                .AsNoTracking()
                .FirstOrDefaultAsync(img => img.Id == winningAttempt.AcceptedArtifactId.Value
                                            && img.SessionId == request.SessionId
                                            && img.LifecycleStatus != ArtifactLifecycleStatus.Deleted, cancellationToken);

            if (artifact == null)
            {
                _logger.LogError("[GetTurnImageGenerationStatus] State divergence detected: Winning attempt {AttemptId} points to AcceptedArtifactId {ArtifactId} which does not exist in storage.",
                    winningAttempt.Id, winningAttempt.AcceptedArtifactId.Value);

                return Result<VisualGenerationStatusResponse>.Failure(
                    StatusCodes.Status500InternalServerError,
                    $"State divergence: Winning artifact '{winningAttempt.AcceptedArtifactId.Value}' was not found in storage.");
            }

            // Bidirectional Lineage Verification
            if (artifact.GenerationAttemptId != winningAttempt.Id || (artifact.GenerationJobId.HasValue && artifact.GenerationJobId.Value != latestJob.Id))
            {
                _logger.LogError("[GetTurnImageGenerationStatus] Lineage fork detected: Artifact {ArtifactId} has GenerationAttemptId={ArtAttemptId} vs Attempt={AttemptId}, JobId={ArtJobId} vs Job={JobId}",
                    artifact.Id, artifact.GenerationAttemptId, winningAttempt.Id, artifact.GenerationJobId, latestJob.Id);

                return Result<VisualGenerationStatusResponse>.Failure(
                    StatusCodes.Status500InternalServerError,
                    $"State divergence: Lineage fork detected between winning attempt '{winningAttempt.Id}' and artifact '{artifact.Id}'.");
            }
        }
        else if (latestJob.Status == ImageJobStatus.Quarantined)
        {
            if (latestJob.QuarantinedAttemptId.HasValue)
            {
                artifact = await _dbContext.SceneImages
                    .AsNoTracking()
                    .FirstOrDefaultAsync(img => img.SessionId == request.SessionId
                                                && img.GenerationAttemptId == latestJob.QuarantinedAttemptId.Value
                                                && img.LifecycleStatus == ArtifactLifecycleStatus.Quarantined, cancellationToken);
            }

            if (artifact == null)
            {
                artifact = await _dbContext.SceneImages
                    .AsNoTracking()
                    .Where(img => img.SessionId == request.SessionId
                                  && img.GenerationJobId == latestJob.Id
                                  && img.LifecycleStatus == ArtifactLifecycleStatus.Quarantined)
                    .OrderByDescending(img => img.Id)
                    .FirstOrDefaultAsync(cancellationToken);
            }
        }
        else
        {
            artifact = await _dbContext.SceneImages
                .AsNoTracking()
                .Where(img => img.SessionId == request.SessionId
                              && (img.GenerationJobId == latestJob.Id || img.GenerationRequestId == latestJob.GenerationRequestId))
                .OrderByDescending(img => img.Id)
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
