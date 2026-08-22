using Domain.Common;

namespace Domain.Entities;

/// <summary>
/// Immutable visual rendering artifact for a specific SceneRevision within a ChatSession.
/// Key invariant: Exactly one rendered visual artifact per (SessionId, SceneRevision).
/// </summary>
public sealed class SceneImage : BaseEntity
{
    public Guid SessionId { get; private set; }
    public Guid CharacterId { get; private set; }
    public Guid TurnId { get; private set; }
    public int SceneRevision { get; private set; }
    public string ImageUrl { get; private set; } = string.Empty;
    public string? IdentityReferenceUrl { get; private set; }
    public string? PreviousSceneImageUrl { get; private set; }
    public string Prompt { get; private set; } = string.Empty;
    public Guid? GenerationJobId { get; private set; }
    public string Workflow { get; private set; } = "VisualIdentity";
    public int WorkflowVersion { get; private set; } = 1;

    private SceneImage() { } // EF Core

    public SceneImage(
        Guid sessionId,
        Guid characterId,
        Guid turnId,
        int sceneRevision,
        string imageUrl,
        string prompt,
        string? identityReferenceUrl = null,
        string? previousSceneImageUrl = null,
        Guid? generationJobId = null,
        string workflow = "VisualIdentity",
        int workflowVersion = 1)
    {
        SessionId = sessionId;
        CharacterId = characterId;
        TurnId = turnId;
        SceneRevision = sceneRevision;
        ImageUrl = imageUrl;
        Prompt = prompt;
        IdentityReferenceUrl = identityReferenceUrl;
        PreviousSceneImageUrl = previousSceneImageUrl;
        GenerationJobId = generationJobId;
        Workflow = workflow;
        WorkflowVersion = workflowVersion;
    }
}
