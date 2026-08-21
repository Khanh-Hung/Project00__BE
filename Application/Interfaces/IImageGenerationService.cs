namespace Application.Interfaces;

/// <summary>
/// Authoritative image generation contract supporting dual-reference conditioning and configurable conditioning scale hierarchy.
/// </summary>
public sealed record ImageGenerationRequest(
    string Prompt,
    int Width = 1024,
    int Height = 1024,
    string? AspectRatio = "16:9",
    string? ReferenceImageUrl = null,
    string? PreviousSceneImageUrl = null,
    string? NegativePrompt = null,
    float? IdentityScale = null,
    float? SceneScale = null,
    int? Steps = null,
    float? GuidanceScale = null,
    long? Seed = null
);

public interface IImageGenerationService
{
    Task<string> GenerateImageAsync(string prompt, int width = 512, int height = 512, CancellationToken ct = default);
    Task<string> GenerateImageAsync(ImageGenerationRequest request, CancellationToken ct = default);
}
