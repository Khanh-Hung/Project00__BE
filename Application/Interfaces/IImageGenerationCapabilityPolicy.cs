using Domain.ValueObjects;

namespace Application.Interfaces;

/// <summary>
/// Authoritative application-boundary policy defining supported image generation capabilities.
/// Evaluates compatibility of (Model, Workflow, WorkflowVersion) tuples before provider invocation.
/// </summary>
public interface IImageGenerationCapabilityPolicy
{
    bool IsSupported(ImageGenerationCapability capability);
}
