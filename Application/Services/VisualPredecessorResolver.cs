using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Application.Services;

/// <summary>
/// Authoritative resolver for Slot 2 (Scene Continuity) predecessor reference images.
/// Follows strict 4-tier resolution priority:
/// 1. Explicit predecessor from VisualSnapshot
/// 2. Latest accepted current artifact of the session
/// 3. Character canonical reference
/// 4. No predecessor (null)
/// Invariants: Never resolves a Quarantined, Failed, Cancelled, or Cross-Session artifact.
/// </summary>
public sealed class VisualPredecessorResolver : IVisualPredecessorResolver
{
    private readonly ProjectDbContext _dbContext;
    private readonly ILogger<VisualPredecessorResolver> _logger;

    public VisualPredecessorResolver(
        ProjectDbContext dbContext,
        ILogger<VisualPredecessorResolver> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<VisualPredecessor?> ResolveAsync(
        Guid sessionId,
        Guid turnId,
        VisualSnapshot snapshot,
        CancellationToken ct = default)
    {
        if (snapshot == null)
            throw new ArgumentNullException(nameof(snapshot));

        // 1. Tier 1: Explicit Predecessor from VisualSnapshot (PreviousSceneImageUrl or PredecessorSceneImageId)
        var explicitUrl = snapshot.PreviousSceneImageUrl;
        if (!string.IsNullOrWhiteSpace(explicitUrl))
        {
            // Verify if explicit URL points to an artifact in the database
            var matchingArtifact = await _dbContext.SceneImages
                .AsNoTracking()
                .FirstOrDefaultAsync(img => img.ImageUrl == explicitUrl, ct);

            if (matchingArtifact != null)
            {
                // Invariant: Cross-session predecessor is strictly forbidden
                if (matchingArtifact.SessionId != sessionId)
                {
                    _logger.LogWarning("[VisualPredecessorResolver] Explicit predecessor artifact {ArtifactId} belongs to Session {ArtifactSession}, not target Session {SessionId}. Rejecting.",
                        matchingArtifact.Id, matchingArtifact.SessionId, sessionId);
                }
                // Invariant: Quarantined or deleted artifact is strictly forbidden
                else if (matchingArtifact.LifecycleStatus == ArtifactLifecycleStatus.Quarantined
                         || matchingArtifact.LifecycleStatus == ArtifactLifecycleStatus.Deleted)
                {
                    _logger.LogWarning("[VisualPredecessorResolver] Explicit predecessor artifact {ArtifactId} is in invalid status {Status}. Rejecting.",
                        matchingArtifact.Id, matchingArtifact.LifecycleStatus);
                }
                else
                {
                    _logger.LogInformation("[VisualPredecessorResolver] Resolved explicit predecessor from VisualSnapshot for Session {SessionId}: ArtifactId={ArtifactId}",
                        sessionId, matchingArtifact.Id);
                    return new VisualPredecessor(matchingArtifact.Id, matchingArtifact.ImageUrl, "SnapshotExplicit", matchingArtifact.VisualRevision);
                }
            }
            else
            {
                // Non-DB URL (e.g. externally supplied valid image)
                return new VisualPredecessor(null, explicitUrl, "SnapshotExplicit");
            }
        }

        // 2. Tier 2: Latest Accepted Current Artifact of the Session (via VisualSessionState or SceneImages)
        var sessionState = await _dbContext.VisualSessionStates
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.SessionId == sessionId, ct);

        if (sessionState?.CurrentImageId != null)
        {
            var currentArtifact = await _dbContext.SceneImages
                .AsNoTracking()
                .FirstOrDefaultAsync(img => img.Id == sessionState.CurrentImageId.Value, ct);

            if (currentArtifact != null
                && currentArtifact.SessionId == sessionId
                && currentArtifact.LifecycleStatus != ArtifactLifecycleStatus.Quarantined
                && currentArtifact.LifecycleStatus != ArtifactLifecycleStatus.Deleted)
            {
                _logger.LogInformation("[VisualPredecessorResolver] Resolved current session artifact predecessor for Session {SessionId}: ArtifactId={ArtifactId}, VisualRevision={VisualRevision}",
                    sessionId, currentArtifact.Id, sessionState.VisualRevision);
                return new VisualPredecessor(currentArtifact.Id, currentArtifact.ImageUrl, "CurrentSessionArtifact", sessionState.VisualRevision);
            }
        }

        // Fallback query directly on SceneImages for active current image
        var fallbackCurrentArtifact = await _dbContext.SceneImages
            .AsNoTracking()
            .Where(img => img.SessionId == sessionId
                          && img.IsCurrent
                          && img.LifecycleStatus != ArtifactLifecycleStatus.Quarantined
                          && img.LifecycleStatus != ArtifactLifecycleStatus.Deleted)
            .OrderByDescending(img => img.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (fallbackCurrentArtifact != null)
        {
            _logger.LogInformation("[VisualPredecessorResolver] Resolved fallback active session artifact for Session {SessionId}: ArtifactId={ArtifactId}",
                sessionId, fallbackCurrentArtifact.Id);
            return new VisualPredecessor(fallbackCurrentArtifact.Id, fallbackCurrentArtifact.ImageUrl, "CurrentSessionArtifact", fallbackCurrentArtifact.VisualRevision);
        }

        // 3. Tier 3: Character Canonical Reference
        var canonicalRef = snapshot.IdentityReferenceUrl ?? snapshot.VisualIdentity?.CanonicalReferenceUrl;
        if (!string.IsNullOrWhiteSpace(canonicalRef))
        {
            _logger.LogInformation("[VisualPredecessorResolver] Resolved character canonical reference predecessor for Session {SessionId}", sessionId);
            return new VisualPredecessor(null, canonicalRef, "CharacterCanonicalReference");
        }

        // 4. Tier 4: No Predecessor Available
        _logger.LogInformation("[VisualPredecessorResolver] No predecessor available for Session {SessionId}, Turn {TurnId}", sessionId, turnId);
        return null;
    }
}
