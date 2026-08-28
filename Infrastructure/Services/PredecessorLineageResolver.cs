using Application.DTOs;
using Application.Exceptions;
using Application.Interfaces;
using Domain.Enums;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services;

/// <summary>
/// Authoritative implementation of predecessor visual reference resolution.
/// Enforces P0-2: Predecessor reference URLs are strictly drawn from confirmed, accepted SceneImage records with IsCurrent = true.
/// </summary>
public sealed class PredecessorLineageResolver : IPredecessorLineageResolver
{
    private readonly CoreDbContext _dbContext;
    private readonly ILogger<PredecessorLineageResolver> _logger;

    public PredecessorLineageResolver(
        CoreDbContext dbContext,
        ILogger<PredecessorLineageResolver> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<(bool IsReady, string? PredecessorImageUrl, string? DeferReason)> ResolvePredecessorReferenceAsync(
        Guid sessionId,
        int currentRevision,
        int? explicitPredecessorRevision,
        string? fallbackImageUrl,
        CancellationToken ct = default)
    {
        if (currentRevision <= 1)
        {
            return (IsReady: true, PredecessorImageUrl: fallbackImageUrl, DeferReason: null);
        }

        var predRev = explicitPredecessorRevision ?? (currentRevision - 1);

        // Authoritative query: Only confirmed accepted artifacts with IsCurrent = true
        var predecessorArtifact = await _dbContext.SceneImages
            .FirstOrDefaultAsync(img => img.SessionId == sessionId && img.SceneRevision == predRev && img.IsCurrent, ct);

        if (predecessorArtifact != null)
        {
            return (IsReady: true, PredecessorImageUrl: predecessorArtifact.ImageUrl, DeferReason: null);
        }

        // Predecessor artifact missing: Check if predecessor failed permanently in ImageGenerationJobs
        var predJob = await _dbContext.ImageGenerationJobs
            .Where(j => j.SessionId == sessionId && j.SceneRevision == predRev)
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (predJob != null && predJob.Status == ImageJobStatus.Failed && !predJob.IsRetryable)
        {
            _logger.LogWarning("[PredecessorLineageResolver] Blocking Revision {Revision} because predecessor Revision {PredRev} permanently failed.",
                currentRevision, predRev);
            throw new GpuNonTransientException($"Predecessor Revision {predRev} failed permanently.");
        }

        // Check predecessor in Outbox if job entity not yet created or failed at outbox level
        var predecessorMsg = await _dbContext.OutboxMessages
            .Where(m => m.EventType == OutboxEventTypes.SceneImageGeneration)
            .ToListAsync(ct);

        var predMatchingMsg = predecessorMsg.FirstOrDefault(m =>
        {
            try
            {
                var p = System.Text.Json.JsonSerializer.Deserialize<SceneImageGenerationOutboxPayload>(m.PayloadJson);
                return p?.Snapshot?.SessionId == sessionId && p?.Snapshot?.SceneRevision == predRev;
            }
            catch { return false; }
        });

        if (predMatchingMsg != null && predMatchingMsg.Status == OutboxStatus.Failed)
        {
            _logger.LogWarning("[PredecessorLineageResolver] Blocking Revision {Revision} because predecessor Revision {PredRev} failed permanently in Outbox.",
                currentRevision, predRev);
            throw new GpuNonTransientException($"Predecessor Revision {predRev} failed permanently.");
        }

        _logger.LogInformation("[PredecessorLineageResolver] Deferring Revision {Revision} because predecessor Revision {PredRev} is not yet accepted/current.",
            currentRevision, predRev);
        return (IsReady: false, PredecessorImageUrl: null, DeferReason: $"Predecessor Revision {predRev} not yet completed");
    }
}
