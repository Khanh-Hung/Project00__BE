using Domain.Common;
using Domain.Common.DateTimes;

namespace Domain.Entities;

/// <summary>
/// Authoritative visual state of a ChatSession tracking the currently active visual artifact,
/// the current generation job, and the monotonic visual revision number.
/// Invariants:
/// 1. Exactly one VisualSessionState per ChatSession (SessionId is PK).
/// 2. CurrentImageId must reference an accepted, non-quarantined SceneImage.
/// 3. VisualRevision increments monotonically upon each accepted visual artifact promotion.
/// </summary>
public sealed class VisualSessionState : Entity
{
    public Guid SessionId { get; private set; }
    public Guid? CurrentImageId { get; private set; }
    public Guid? CurrentGenerationJobId { get; private set; }
    public int VisualRevision { get; private set; }

    private VisualSessionState() { } // EF Core

    public VisualSessionState(
        Guid sessionId,
        Guid? currentImageId = null,
        Guid? currentGenerationJobId = null,
        int visualRevision = 1,
        DateTime? updatedAt = null) : base(sessionId)
    {
        SessionId = sessionId;
        CurrentImageId = currentImageId;
        CurrentGenerationJobId = currentGenerationJobId;
        VisualRevision = Math.Max(1, visualRevision);
        SetUpdated(updatedAt ?? Clock.Now);
    }

    /// <summary>
    /// Atomically promotes an accepted artifact to be the current visual state for the session,
    /// advancing the visual revision counter.
    /// </summary>
    public void PromoteArtifact(Guid artifactId, Guid generationJobId, DateTime now)
    {
        if (artifactId == Guid.Empty)
            throw new ArgumentException("ArtifactId cannot be empty.", nameof(artifactId));
        if (generationJobId == Guid.Empty)
            throw new ArgumentException("GenerationJobId cannot be empty.", nameof(generationJobId));

        CurrentImageId = artifactId;
        CurrentGenerationJobId = generationJobId;
        VisualRevision++;
        SetUpdated(now);
    }

    /// <summary>
    /// Clears the current visual state if the active image is removed or invalidated.
    /// </summary>
    public void ClearCurrent(DateTime now)
    {
        CurrentImageId = null;
        CurrentGenerationJobId = null;
        SetUpdated(now);
    }
}
