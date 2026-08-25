namespace Domain.ValueObjects;

/// <summary>
/// Structured cinematic composition and narrative scene description for a conversation turn.
/// Frozen inside VisualSnapshot to guarantee deterministic downstream prompt compilation and image synthesis.
/// </summary>
public sealed record VisualSceneDescription(
    string? ShotType = null,             // e.g. "medium shot", "upper body portrait", "close-up portrait", "cowboy shot"
    string? CameraAngle = null,          // e.g. "eye level", "slight 3/4 turn", "dynamic side angle", "low angle"
    string? SubjectPlacement = null,     // e.g. "centered", "left third", "right third"
    string? DetailedAction = null,       // Concrete physical action in English (e.g. "stepping forward, covering technical blueprint with hand")
    string? DetailedEnvironment = null,  // Concrete background setting in English (e.g. "steampunk mechanical workshop, copper pipes, brass gears, workbench")
    string? LightingStyle = null,        // Lighting style in English (e.g. "warm indoor lantern light, cool window rim light, ethereal magical particles")
    string? Atmosphere = null,           // Emotional scene atmosphere (e.g. "cautious tension, inquisitive, cozy intimate")
    List<string>? EnglishPromptTags = null // Curated English prompt tags for Stable Diffusion CLIP
);
