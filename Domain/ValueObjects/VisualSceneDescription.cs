using System.Collections.Immutable;

namespace Domain.ValueObjects;

/// <summary>
/// Structured cinematic composition and narrative scene description for a conversation turn.
/// Frozen inside VisualSnapshot to guarantee deterministic downstream prompt compilation and image synthesis.
/// Deep-immutable record with zero mutable collections.
/// </summary>
public sealed record VisualSceneDescription
{
    public string? ShotType { get; init; }
    public string? CameraAngle { get; init; }
    public string? SubjectPlacement { get; init; }
    public string? DetailedAction { get; init; }
    public string? DetailedEnvironment { get; init; }
    public string? LightingStyle { get; init; }
    public string? Atmosphere { get; init; }
    public ImmutableArray<string> EnglishPromptTags { get; init; }

    public VisualSceneDescription(
        string? shotType = null,
        string? cameraAngle = null,
        string? subjectPlacement = null,
        string? detailedAction = null,
        string? detailedEnvironment = null,
        string? lightingStyle = null,
        string? atmosphere = null,
        IEnumerable<string>? englishPromptTags = null)
    {
        ShotType = shotType;
        CameraAngle = cameraAngle;
        SubjectPlacement = subjectPlacement;
        DetailedAction = detailedAction;
        DetailedEnvironment = detailedEnvironment;
        LightingStyle = lightingStyle;
        Atmosphere = atmosphere;
        EnglishPromptTags = englishPromptTags != null
            ? englishPromptTags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToImmutableArray()
            : ImmutableArray<string>.Empty;
    }
}
