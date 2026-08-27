using Application.Abstractions.Responses;
using Application.DTOs;
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

    public GetTurnImageGenerationStatusHandler(ProjectDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<Result<VisualGenerationStatusResponse>> Handle(GetTurnImageGenerationStatusQuery request, CancellationToken cancellationToken)
    {
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

        var artifact = await _dbContext.SceneImages
            .AsNoTracking()
            .FirstOrDefaultAsync(img => img.GenerationJobId == latestJob.Id || img.GenerationRequestId == latestJob.GenerationRequestId, cancellationToken);

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
