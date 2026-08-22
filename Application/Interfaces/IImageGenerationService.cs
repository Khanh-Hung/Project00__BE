using Domain.ValueObjects;

namespace Application.Interfaces;

/// <summary>
/// Authoritative image generation contract supporting dual-reference conditioning and configurable conditioning scale hierarchy.
/// </summary>
public sealed record ImageGenerationRequest(
    string Prompt,
    int Width = 512,
    int Height = 768,
    string? AspectRatio = "2:3",
    string? ReferenceImageUrl = null,
    string? PreviousSceneImageUrl = null,
    string? NegativePrompt = null,
    float? IdentityScale = null,
    float? SceneScale = null,
    int? Steps = null,
    float? GuidanceScale = null,
    long? Seed = null,
    string? Model = null,
    string? Sampler = null,
    string? Scheduler = null,
    float? IPAdapterWeight = null,
    float? IPAdapterEndAt = null,
    string Workflow = "VisualIdentity",
    int WorkflowVersion = 1,
    string? ParametersJson = null,
    Dictionary<string, object>? ExtraParameters = null
)
{
    public static ImageGenerationRequest FromSnapshot(
        VisualSnapshot snapshot,
        string compiledPrompt,
        string? compiledNegative = null,
        string? previousSceneImageUrlOverride = null)
    {
        var profile = snapshot.GenerationProfile ?? GenerationProfile.CreateDefault();
        return new ImageGenerationRequest(
            Prompt: compiledPrompt,
            NegativePrompt: compiledNegative ?? snapshot.NegativeConstraints,
            Width: profile.Width,
            Height: profile.Height,
            ReferenceImageUrl: snapshot.IdentityReferenceUrl,
            PreviousSceneImageUrl: previousSceneImageUrlOverride ?? snapshot.PreviousSceneImageUrl,
            Steps: profile.Steps,
            GuidanceScale: profile.Cfg,
            Seed: profile.Seed,
            Model: profile.Model,
            Sampler: profile.Sampler,
            Scheduler: profile.Scheduler,
            Workflow: profile.Workflow,
            WorkflowVersion: profile.WorkflowVersion,
            ParametersJson: profile.ParametersJson
        );
    }
}

public sealed record ImageGenerationResult(
    string ImageUrl,
    string Provider,
    string? ProviderJobId,
    long DurationMs,
    long Seed,
    string? MetadataJson = null
);

public interface IImageGenerationService
{
    Task<string> GenerateImageAsync(string prompt, int width = 512, int height = 512, CancellationToken ct = default);
    Task<string> GenerateImageAsync(ImageGenerationRequest request, CancellationToken ct = default);
    Task<ImageGenerationResult> GenerateImageWithResultAsync(ImageGenerationRequest request, CancellationToken ct = default)
    {
        return GenerateImageAsync(request, ct).ContinueWith(t => new ImageGenerationResult(
            ImageUrl: t.Result,
            Provider: "Default",
            ProviderJobId: null,
            DurationMs: 0,
            Seed: request.Seed ?? 0
        ), ct);
    }
}
