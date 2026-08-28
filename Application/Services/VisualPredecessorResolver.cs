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
/// 1. Explicit predecessor from VisualSnapshot (PredecessorSceneImageId authoritative, then PreviousSceneImageUrl)
/// 2. Latest accepted current artifact of the session (only when no explicit predecessor specified)
/// 3. Character canonical reference
/// 4. No predecessor (null)
/// Invariants:
/// - Never resolves a Quarantined, Failed, Cancelled, or Cross-Session artifact.
/// - If an explicit predecessor is specified but invalid, strictly REJECTS (returns null) rather than silently falling back.
/// </summary>
public sealed class VisualPredecessorResolver : IVisualPredecessorResolver
{
    private readonly CoreDbContext _dbContext;
    private readonly ILogger<VisualPredecessorResolver> _logger;

    public VisualPredecessorResolver(
        CoreDbContext dbContext,
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

        bool hasExplicitPredecessor = false;

        // 1. Tier 1A: Authoritative Explicit Predecessor ID from VisualSnapshot
        if (snapshot.PredecessorSceneImageId.HasValue && snapshot.PredecessorSceneImageId.Value != Guid.Empty)
        {
            hasExplicitPredecessor = true;
            var explicitId = snapshot.PredecessorSceneImageId.Value;

            var matchingArtifact = await _dbContext.SceneImages
                .AsNoTracking()
                .FirstOrDefaultAsync(img => img.Id == explicitId, ct);

            if (matchingArtifact == null)
            {
                _logger.LogWarning("[VisualPredecessorResolver] Explicit predecessor artifact ID {ArtifactId} not found in database for Session {SessionId}. Strict reject.",
                    explicitId, sessionId);
                return null;
            }

            // Invariant: Cross-session predecessor is strictly forbidden
            if (matchingArtifact.SessionId != sessionId)
            {
                _logger.LogWarning("[VisualPredecessorResolver] Explicit predecessor artifact {ArtifactId} belongs to foreign Session {ArtifactSession}, not target Session {SessionId}. Strict reject.",
                    matchingArtifact.Id, matchingArtifact.SessionId, sessionId);
                return null;
            }

            // Invariant: Quarantined or deleted artifact is strictly forbidden
            if (matchingArtifact.LifecycleStatus == ArtifactLifecycleStatus.Quarantined
                || matchingArtifact.LifecycleStatus == ArtifactLifecycleStatus.Deleted)
            {
                _logger.LogWarning("[VisualPredecessorResolver] Explicit predecessor artifact {ArtifactId} is in invalid status {Status}. Strict reject.",
                    matchingArtifact.Id, matchingArtifact.LifecycleStatus);
                return null;
            }

            _logger.LogInformation("[VisualPredecessorResolver] Resolved explicit predecessor by ID for Session {SessionId}: ArtifactId={ArtifactId}, Revision={Revision}",
                sessionId, matchingArtifact.Id, matchingArtifact.VisualRevision);

            return new VisualPredecessor(matchingArtifact.Id, matchingArtifact.ImageUrl, "SnapshotExplicitId", matchingArtifact.VisualRevision);
        }

        // 1. Tier 1B: Explicit Predecessor URL from VisualSnapshot
        if (!string.IsNullOrWhiteSpace(snapshot.PreviousSceneImageUrl))
        {
            hasExplicitPredecessor = true;
            var explicitUrl = snapshot.PreviousSceneImageUrl;

            var matchingArtifact = await _dbContext.SceneImages
                .AsNoTracking()
                .FirstOrDefaultAsync(img => img.ImageUrl == explicitUrl, ct);

            if (matchingArtifact != null)
            {
                if (matchingArtifact.SessionId != sessionId)
                {
                    _logger.LogWarning("[VisualPredecessorResolver] Explicit predecessor URL belongs to foreign Session {ArtifactSession}. Strict reject.",
                        matchingArtifact.SessionId);
                    return null;
                }

                if (matchingArtifact.LifecycleStatus == ArtifactLifecycleStatus.Quarantined
                    || matchingArtifact.LifecycleStatus == ArtifactLifecycleStatus.Deleted)
                {
                    _logger.LogWarning("[VisualPredecessorResolver] Explicit predecessor URL artifact {ArtifactId} is in invalid status {Status}. Strict reject.",
                        matchingArtifact.Id, matchingArtifact.LifecycleStatus);
                    return null;
                }

                return new VisualPredecessor(matchingArtifact.Id, matchingArtifact.ImageUrl, "SnapshotExplicitUrl", matchingArtifact.VisualRevision);
            }
            else
            {
                // External valid URL supplied without local DB entity
                return new VisualPredecessor(null, explicitUrl, "SnapshotExplicitUrl");
            }
        }

        // If an explicit predecessor was specified but failed validation, we strictly returned null above.
        // We only proceed to Tier 2 & Tier 3 if NO explicit predecessor was requested on the snapshot.
        if (hasExplicitPredecessor)
        {
            return null;
        }

        // 2. Tier 2: Latest Accepted Current Artifact of the Session (via VisualSessionState)
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
