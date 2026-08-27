using Application.Abstractions.Responses;
using Application.DTOs;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Application.Features.Chat.Queries.VisualSession;

public sealed record GetCurrentVisualStateQuery(Guid SessionId) : IRequest<Result<VisualArtifactResponse>>;

public sealed class GetCurrentVisualStateHandler : IRequestHandler<GetCurrentVisualStateQuery, Result<VisualArtifactResponse>>
{
    private readonly ProjectDbContext _dbContext;

    public GetCurrentVisualStateHandler(ProjectDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<Result<VisualArtifactResponse>> Handle(GetCurrentVisualStateQuery request, CancellationToken cancellationToken)
    {
        var sessionState = await _dbContext.VisualSessionStates
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.SessionId == request.SessionId, cancellationToken);

        SceneImage? currentArtifact = null;

        if (sessionState?.CurrentImageId != null)
        {
            currentArtifact = await _dbContext.SceneImages
                .AsNoTracking()
                .FirstOrDefaultAsync(img => img.Id == sessionState.CurrentImageId.Value
                                            && img.SessionId == request.SessionId
                                            && img.LifecycleStatus != ArtifactLifecycleStatus.Quarantined
                                            && img.LifecycleStatus != ArtifactLifecycleStatus.Deleted, cancellationToken);
        }

        if (currentArtifact == null)
        {
            currentArtifact = await _dbContext.SceneImages
                .AsNoTracking()
                .Where(img => img.SessionId == request.SessionId
                              && img.IsCurrent
                              && img.LifecycleStatus != ArtifactLifecycleStatus.Quarantined
                              && img.LifecycleStatus != ArtifactLifecycleStatus.Deleted)
                .OrderByDescending(img => img.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (currentArtifact == null)
        {
            return Result<VisualArtifactResponse>.Failure(
                StatusCodes.Status404NotFound,
                $"No active visual artifact found for session '{request.SessionId}'.");
        }

        var response = new VisualArtifactResponse(
            ArtifactId: currentArtifact.Id,
            TurnId: currentArtifact.TurnId,
            SessionId: currentArtifact.SessionId,
            ImageUrl: currentArtifact.ImageUrl,
            IsCurrent: currentArtifact.IsCurrent && currentArtifact.LifecycleStatus == ArtifactLifecycleStatus.Current,
            VisualRevision: sessionState?.VisualRevision ?? currentArtifact.VisualRevision,
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
