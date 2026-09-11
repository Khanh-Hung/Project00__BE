using Application.Interfaces;
using Domain.ValueObjects;
using Infrastructure.ImageGeneration.ComfyUI;

namespace Infrastructure.ImageGeneration;

/// <summary>
/// Authoritative capability policy implementation delegating to registered workflow builders.
/// Ensures workflow builders remain the single source of truth for (Model, Workflow, WorkflowVersion) compatibility.
/// </summary>
public sealed class WorkflowCapabilityPolicy : IImageGenerationCapabilityPolicy
{
    private readonly IEnumerable<IComfyUIWorkflowBuilder> _builders;

    public WorkflowCapabilityPolicy(IEnumerable<IComfyUIWorkflowBuilder> builders)
    {
        _builders = builders ?? throw new ArgumentNullException(nameof(builders));
    }

    public bool IsSupported(ImageGenerationCapability capability)
    {
        if (capability == null || string.IsNullOrWhiteSpace(capability.Model))
            return false;

        return _builders.Any(b => b.CanHandle(capability));
    }

    public bool SupportsIdentityConditioning(ImageGenerationCapability capability)
    {
        if (capability == null || string.IsNullOrWhiteSpace(capability.Model))
            return false;

        return _builders.Any(b => b.CanHandle(capability) && b.SupportsIdentityConditioning);
    }

    /// <summary>
    /// Creates a default capability policy populated with the built-in standard workflow builders.
    /// Provides a single authoritative default for test fixtures and composition fallbacks.
    /// </summary>
    public static WorkflowCapabilityPolicy CreateDefault() =>
        new(new IComfyUIWorkflowBuilder[]
        {
            new VisualIdentityWorkflowV1Builder(),
            new VisualContinuityWorkflowV2Builder(),
            new TextToImageWorkflowV1Builder()
        });
}
