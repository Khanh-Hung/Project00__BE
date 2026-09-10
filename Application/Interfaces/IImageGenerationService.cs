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
    string? Workflow = null,
    int WorkflowVersion = 1,
    string? ParametersJson = null,
    string? ProviderJobId = null,
    Func<string, CancellationToken, Task>? OnPromptQueuedAsync = null,
    Dictionary<string, object>? ExtraParameters = null,
    IdentityConditioningIntent? IdentityConditioning = null
)
{
    /// <summary>
    /// Gets the capability tuple (Model, Workflow, WorkflowVersion) for this request.
    /// </summary>
    public ImageGenerationCapability Capability => new(Model ?? string.Empty, Workflow ?? string.Empty, WorkflowVersion);

    /// <summary>
    /// Gets the effective model-agnostic identity conditioning intent for this generation request.
    /// Guaranteed non-null: falls back to intent derived from ReferenceImageUrl and PreviousSceneImageUrl.
    /// </summary>
    public IdentityConditioningIntent EffectiveIdentityConditioning =>
        IdentityConditioning ?? IdentityConditioningIntent.FromReferences(
            canonicalReferenceUrl: ReferenceImageUrl,
            previousSceneReferenceUrl: PreviousSceneImageUrl,
            preservationStrength: IdentityScale
        );

    /// <summary>
    /// Resolves the effective generation capability required for this request.
    /// Preserves explicit caller workflow selection without silent rewriting.
    /// When workflow is unspecified (null or whitespace), automatically resolves the appropriate capability
    /// from the authoritative EffectiveIdentityConditioning intent:
    /// - HasPreviousSceneReference => VisualContinuity v2
    /// - HasCanonicalReference or IsRequired => VisualIdentity v1
    /// - No conditioning => TextToImage v1
    /// </summary>
    public ImageGenerationCapability ResolveEffectiveCapability()
    {
        var conditioning = EffectiveIdentityConditioning;

        // 1. If workflow is genuinely unspecified, resolve from authoritative conditioning intent
        if (string.IsNullOrWhiteSpace(Workflow))
        {
            if (conditioning.HasPreviousSceneReference)
            {
                return new ImageGenerationCapability(Model ?? string.Empty, "VisualContinuity", 2);
            }

            if (conditioning.HasCanonicalReference || conditioning.IsRequired)
            {
                return new ImageGenerationCapability(Model ?? string.Empty, "VisualIdentity", 1);
            }

            return new ImageGenerationCapability(Model ?? string.Empty, "TextToImage", 1);
        }

        var targetWorkflow = Workflow.Trim();
        var targetVersion = WorkflowVersion;

        // 2. Backward compatibility: if VisualIdentity was specified but no identity conditioning/reference exists,
        // map to TextToImage v1 (matching PR63 baseline behavior)
        var hasIdentityRef = !string.IsNullOrWhiteSpace(ReferenceImageUrl) || conditioning.IsRequired;
        if (!hasIdentityRef && string.Equals(targetWorkflow, "VisualIdentity", StringComparison.OrdinalIgnoreCase))
        {
            return new ImageGenerationCapability(Model ?? string.Empty, "TextToImage", 1);
        }

        // 3. Explicit workflow chosen by caller MUST be preserved without silent override
        return new ImageGenerationCapability(Model ?? string.Empty, targetWorkflow, targetVersion);
    }

    /// <summary>
    /// Validates capability compatibility and identity conditioning satisfaction at the Application boundary before submitting to provider.
    /// </summary>
    public void ValidateCapability(IImageGenerationCapabilityPolicy capabilityPolicy)
    {
        ArgumentNullException.ThrowIfNull(capabilityPolicy);
        var capability = ResolveEffectiveCapability();
        if (!capabilityPolicy.IsSupported(capability))
        {
            throw new Application.Exceptions.GpuNonTransientException(
                $"Generation capability '{capability}' is not supported. Workflow '{capability.Workflow}' v{capability.WorkflowVersion} is not compatible with model '{Model}'.");
        }

        var conditioning = EffectiveIdentityConditioning;
        if (conditioning.IsRequired && !capabilityPolicy.SupportsIdentityConditioning(capability))
        {
            throw new Application.Exceptions.GpuNonTransientException(
                $"Generation capability '{capability}' does not support identity conditioning required by this request.");
        }
    }

    public static ImageGenerationRequest FromSnapshot(
        VisualSnapshot snapshot,
        string compiledPrompt,
        string? compiledNegative = null,
        string? previousSceneImageUrlOverride = null,
        string? providerJobId = null,
        Func<string, CancellationToken, Task>? onPromptQueuedAsync = null)
    {
        var profile = snapshot.GenerationProfile;
        var previousSceneUrl = previousSceneImageUrlOverride ?? snapshot.PreviousSceneImageUrl;
        IdentityConditioningIntent conditioningIntent;
        if (snapshot.IdentityConditioning != null)
        {
            if (previousSceneImageUrlOverride != null && previousSceneImageUrlOverride != snapshot.IdentityConditioning.PreviousSceneReferenceUrl)
            {
                var hasAnyRef = !string.IsNullOrWhiteSpace(snapshot.IdentityConditioning.CanonicalReferenceUrl) || !string.IsNullOrWhiteSpace(previousSceneImageUrlOverride);
                conditioningIntent = snapshot.IdentityConditioning with
                {
                    PreviousSceneReferenceUrl = previousSceneImageUrlOverride,
                    IsRequired = hasAnyRef
                };
            }
            else
            {
                conditioningIntent = snapshot.IdentityConditioning;
            }
        }
        else
        {
            conditioningIntent = IdentityConditioningIntent.FromReferences(
                canonicalReferenceUrl: snapshot.IdentityReferenceUrl,
                previousSceneReferenceUrl: previousSceneUrl,
                context: snapshot.Context
            );
        }

        return new ImageGenerationRequest(
            Prompt: compiledPrompt,
            NegativePrompt: compiledNegative ?? snapshot.NegativeConstraints,
            Width: profile.Width,
            Height: profile.Height,
            ReferenceImageUrl: snapshot.IdentityReferenceUrl,
            PreviousSceneImageUrl: previousSceneUrl,
            Steps: profile.Steps,
            GuidanceScale: profile.Cfg,
            Seed: profile.Seed,
            Model: profile.Model,
            Sampler: profile.Sampler,
            Scheduler: profile.Scheduler,
            Workflow: profile.Workflow,
            WorkflowVersion: profile.WorkflowVersion,
            ParametersJson: profile.ParametersJson,
            ProviderJobId: providerJobId,
            OnPromptQueuedAsync: onPromptQueuedAsync,
            IdentityConditioning: conditioningIntent
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
    async Task<ImageGenerationResult> GenerateImageWithResultAsync(ImageGenerationRequest request, CancellationToken ct = default)
    {
        var url = await GenerateImageAsync(request, ct);
        return new ImageGenerationResult(
            ImageUrl: url,
            Provider: "Default",
            ProviderJobId: null,
            DurationMs: 0,
            Seed: request.Seed ?? 0
        );
    }
}
