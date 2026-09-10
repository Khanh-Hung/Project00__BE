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
}
