using Application.DTOs;
using Application.Interfaces;
using Domain.Enums;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Application.Services;

/// <summary>
/// Service providing visual history inspection across chat turns for a session.
/// Guarantees:
/// - Order from newest to oldest (CreatedAt DESC).
/// - Exact lifecycle status mapping.
/// - At most one entry with IsCurrent = true.
/// </summary>
public sealed class VisualHistoryService : IVisualHistoryService
{
    private readonly CoreDbContext _dbContext;
    private readonly ILogger<VisualHistoryService> _logger;

    public VisualHistoryService(
        CoreDbContext dbContext,
        ILogger<VisualHistoryService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<VisualHistoryEntry>> GetSessionVisualHistoryAsync(
        Guid sessionId,
        int? limit = null,
        CancellationToken ct = default)
    {
        var query = _dbContext.SceneImages
            .AsNoTracking()
            .Where(img => img.SessionId == sessionId && img.LifecycleStatus != ArtifactLifecycleStatus.Deleted)
            .OrderByDescending(img => img.CreatedAt);

        var list = limit.HasValue && limit.Value > 0
            ? await query.Take(limit.Value).ToListAsync(ct)
            : await query.ToListAsync(ct);

        _logger.LogInformation("[VisualHistoryService] Retrieved {Count} visual history entries for Session {SessionId}",
            list.Count, sessionId);

        return list.Select(img => new VisualHistoryEntry(
            ArtifactId: img.Id,
            TurnId: img.TurnId,
            GenerationJobId: img.GenerationJobId,
            SceneRevision: img.SceneRevision,
            VisualRevision: img.VisualRevision,
            IsCurrent: img.IsCurrent && img.LifecycleStatus == ArtifactLifecycleStatus.Current,
            IsQuarantined: img.LifecycleStatus == ArtifactLifecycleStatus.Quarantined,
            LifecycleStatus: img.LifecycleStatus.ToString(),
            CreatedAt: img.CreatedAt,
            ImageUrl: img.ImageUrl,
            Prompt: img.Prompt,
            PredecessorArtifactId: img.PredecessorArtifactId
        )).ToList();
    }
}
