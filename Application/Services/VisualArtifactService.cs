using Application.DTOs;
using Application.Interfaces;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Domain.Events;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Application.Services;

/// <summary>
/// Service coordinating authoritative artifact promotion, visual session state advancement,
/// and artifact superseding within the application layer.
/// </summary>
public sealed class VisualArtifactService : IVisualArtifactService
{
    private readonly ProjectDbContext _dbContext;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly ILogger<VisualArtifactService> _logger;

    public VisualArtifactService(
        ProjectDbContext dbContext,
        IDateTimeProvider dateTimeProvider,
        ILogger<VisualArtifactService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _dateTimeProvider = dateTimeProvider ?? throw new ArgumentNullException(nameof(dateTimeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ArtifactAcceptanceResult> PromoteAsync(
        Guid generationJobId,
        Guid attemptId,
        CancellationToken ct = default)
    {
        var job = await _dbContext.ImageGenerationJobs
            .FirstOrDefaultAsync(j => j.Id == generationJobId, ct);

        if (job == null)
        {
            return new ArtifactAcceptanceResult(false, null, 0, "Failed", $"Job {generationJobId} not found.");
        }

        var attempt = await _dbContext.ImageGenerationAttempts
            .FirstOrDefaultAsync(a => a.Id == attemptId, ct);

        if (attempt == null)
        {
            return new ArtifactAcceptanceResult(false, null, 0, "Failed", $"Attempt {attemptId} not found.");
        }

        if (attempt.GenerationJobId != job.Id)
        {
            return new ArtifactAcceptanceResult(false, null, 0, "Failed", $"Attempt {attemptId} does not belong to Job {job.Id}.");
        }

        var now = _dateTimeProvider.UtcNow;

        // Fetch or initialize VisualSessionState
        var sessionState = await _dbContext.VisualSessionStates
            .FirstOrDefaultAsync(s => s.SessionId == job.SessionId, ct);

        int newVisualRevision = (sessionState?.VisualRevision ?? 0) + 1;
        Guid? previousArtifactId = sessionState?.CurrentImageId;

        // Demote previous current artifacts for this session
        await _dbContext.SceneImages
            .Where(img => img.SessionId == job.SessionId && img.IsCurrent)
            .ExecuteUpdateAsync(s => s
                .SetProperty(img => img.IsCurrent, false)
                .SetProperty(img => img.LifecycleStatus, ArtifactLifecycleStatus.Historical)
                .SetProperty(img => img.UpdatedAt, now), ct);

        // Find or locate the artifact for this attempt / job
        var artifact = await _dbContext.SceneImages
            .FirstOrDefaultAsync(img => img.GenerationJobId == job.Id || img.GenerationRequestId == job.GenerationRequestId, ct);

        if (artifact != null)
        {
            artifact.PromoteToCurrent(newVisualRevision);
        }

        // Update or create VisualSessionState
        if (sessionState != null)
        {
            if (artifact != null)
            {
                sessionState.PromoteArtifact(artifact.Id, job.Id, now);
            }
        }
        else if (artifact != null)
        {
            sessionState = new VisualSessionState(job.SessionId, artifact.Id, job.Id, newVisualRevision, now);
            await _dbContext.VisualSessionStates.AddAsync(sessionState, ct);
        }

        // Enqueue Outbox domain events
        if (artifact != null)
        {
            var acceptedEvent = new VisualArtifactAccepted(
                SessionId: job.SessionId,
                TurnId: job.TurnId,
                ArtifactId: artifact.Id,
                GenerationJobId: job.Id,
                VisualRevision: newVisualRevision,
                OccurredAt: now
            );

            await _dbContext.OutboxMessages.AddAsync(new OutboxMessage(
                eventType: "VisualArtifactAccepted",
                payloadJson: JsonSerializer.Serialize(acceptedEvent)
            ), ct);

            if (previousArtifactId.HasValue && previousArtifactId.Value != artifact.Id)
            {
                var supersededEvent = new VisualArtifactSuperseded(
                    SessionId: job.SessionId,
                    TurnId: job.TurnId,
                    PreviousArtifactId: previousArtifactId.Value,
                    NewArtifactId: artifact.Id,
                    NewVisualRevision: newVisualRevision,
                    OccurredAt: now
                );

                await _dbContext.OutboxMessages.AddAsync(new OutboxMessage(
                    eventType: "VisualArtifactSuperseded",
                    payloadJson: JsonSerializer.Serialize(supersededEvent)
                ), ct);
            }
        }

        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation("[VisualArtifactService] Promoted artifact {ArtifactId} for Session {SessionId} to VisualRevision {VisualRevision}",
            artifact?.Id, job.SessionId, newVisualRevision);

        return new ArtifactAcceptanceResult(true, artifact?.Id, newVisualRevision, "Current");
    }
}
