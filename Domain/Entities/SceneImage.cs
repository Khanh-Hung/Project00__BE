using Domain.Common;
using Domain.Enums;
using Domain.ValueObjects;

namespace Domain.Entities;

/// <summary>
/// Immutable visual rendering artifact for a specific generation attempt within a ChatSession.
/// Key invariant: Unique per (SessionId, GenerationRequestId). Supports multiple generation attempts/regenerations per revision.
/// </summary>
public sealed class SceneImage : BaseEntity
{
    public Guid SessionId { get; private set; }
    public Guid CharacterId { get; private set; }
    public Guid TurnId { get; private set; }
    public int SceneRevision { get; private set; }
    public Guid GenerationRequestId { get; private set; }
    public string ImageUrl { get; private set; } = string.Empty;
    public string? IdentityReferenceUrl { get; private set; }
    public string? PreviousSceneImageUrl { get; private set; }
    public string Prompt { get; private set; } = string.Empty;
    public Guid? GenerationJobId { get; private set; }
    public string Workflow { get; private set; } = "VisualIdentity";
    public int WorkflowVersion { get; private set; } = 1;
    public bool IsCurrent { get; private set; } = true;
    public string? GenerationFingerprint { get; private set; }
    public string? ProvenanceJson { get; private set; }

    /// <summary>
    /// Explicit reference to the accepted predecessor artifact used for Slot 2 conditioning.
    /// </summary>
    public Guid? PredecessorArtifactId { get; private set; }

    /// <summary>
    /// The session visual revision number assigned when this artifact was accepted/promoted.
    /// </summary>
    public int VisualRevision { get; private set; } = 1;

    /// <summary>
    /// Lifecycle state of this visual artifact (Candidate, Current, Historical, Quarantined, Deleted).
    /// </summary>
    public ArtifactLifecycleStatus LifecycleStatus { get; private set; } = ArtifactLifecycleStatus.Current;

    /// <summary>
    /// Timestamp when this artifact was placed in Quarantine.
    /// </summary>
    public DateTime? QuarantinedAt { get; private set; }

    private SceneImage() { } // EF Core

    public SceneImage(
        Guid sessionId,
        Guid characterId,
        Guid turnId,
        int sceneRevision,
        string imageUrl,
        string prompt,
        Guid? generationRequestId = null,
        Guid? generationJobId = null,
        string? identityReferenceUrl = null,
        string? previousSceneImageUrl = null,
        string workflow = "VisualIdentity",
        int workflowVersion = 1,
        bool isCurrent = true,
        string? generationFingerprint = null,
        string? provenanceJson = null,
        Guid? predecessorArtifactId = null,
        int visualRevision = 1,
        ArtifactLifecycleStatus? lifecycleStatus = null,
        DateTime? quarantinedAt = null,
        Guid? id = null)
    {
        if (id.HasValue && id.Value != Guid.Empty)
        {
            Id = id.Value;
        }

        SessionId = sessionId;
        CharacterId = characterId;
        TurnId = turnId;
        SceneRevision = sceneRevision;
        GenerationRequestId = generationRequestId ?? turnId;
        GenerationJobId = generationJobId;
        ImageUrl = imageUrl;
        Prompt = prompt;
        IdentityReferenceUrl = identityReferenceUrl;
        PreviousSceneImageUrl = previousSceneImageUrl;
        Workflow = workflow;
        WorkflowVersion = workflowVersion;
        IsCurrent = isCurrent;
        GenerationFingerprint = generationFingerprint;
        ProvenanceJson = provenanceJson;
        PredecessorArtifactId = predecessorArtifactId;
        VisualRevision = Math.Max(1, visualRevision);
        LifecycleStatus = lifecycleStatus ?? (isCurrent ? ArtifactLifecycleStatus.Current : ArtifactLifecycleStatus.Historical);
        QuarantinedAt = quarantinedAt;
    }

    public void AttachProvenance(GenerationProvenance provenance)
    {
        ProvenanceJson = provenance?.ToJson();
        Touch();
    }

    public GenerationProvenance? GetProvenance() => GenerationProvenance.FromJson(ProvenanceJson);

    public void SetCurrent(bool isCurrent)
    {
        IsCurrent = isCurrent;
        LifecycleStatus = isCurrent ? ArtifactLifecycleStatus.Current : ArtifactLifecycleStatus.Historical;
        Touch();
    }

    public void PromoteToCurrent(int visualRevision, Guid? predecessorArtifactId = null)
    {
        IsCurrent = true;
        LifecycleStatus = ArtifactLifecycleStatus.Current;
        VisualRevision = visualRevision;
        if (predecessorArtifactId.HasValue)
        {
            PredecessorArtifactId = predecessorArtifactId.Value;
        }
        Touch();
    }

    public void DemoteCurrent()
    {
        IsCurrent = false;
        LifecycleStatus = ArtifactLifecycleStatus.Historical;
        Touch();
    }

    public void MarkQuarantined(DateTime? quarantinedAt = null)
    {
        IsCurrent = false;
        LifecycleStatus = ArtifactLifecycleStatus.Quarantined;
        QuarantinedAt = quarantinedAt ?? DateTime.UtcNow;
        Touch();
    }

    public void MarkDeleted()
    {
        IsCurrent = false;
        LifecycleStatus = ArtifactLifecycleStatus.Deleted;
        Touch();
    }
}
