using Application.Interfaces;
using Domain.ValueObjects;

namespace Infrastructure.ImageGeneration.ComfyUI;

public interface IComfyUIWorkflowBuilder
{
    string WorkflowName { get; }
    int WorkflowVersion { get; }
    IReadOnlySet<string> SupportedModels { get; }
    bool CanHandle(string workflow, int workflowVersion, string? model);

    bool CanHandle(ImageGenerationCapability capability)
        => CanHandle(capability.Workflow, capability.WorkflowVersion, capability.Model);

    /// <summary>
    /// Indicates whether this workflow builder supports character visual identity and continuity conditioning.
    /// Default is false (e.g. text-to-image workflows).
    /// </summary>
    bool SupportsIdentityConditioning => false;

    Dictionary<string, object> BuildWorkflow(ImageGenerationRequest request, string resolvedReferenceImageName);

    Dictionary<string, object> BuildWorkflow(
        ImageGenerationRequest request,
        string resolvedReferenceImageName,
        string? resolvedPreviousSceneImageName)
    {
        return BuildWorkflow(request, resolvedReferenceImageName);
    }
}
