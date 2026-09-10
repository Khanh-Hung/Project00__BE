using Application.Interfaces;

namespace Infrastructure.ImageGeneration.ComfyUI;

public interface IComfyUIWorkflowBuilder
{
    string WorkflowName { get; }
    int WorkflowVersion { get; }
    IReadOnlySet<string> SupportedModels { get; }
    bool CanHandle(string workflow, int workflowVersion, string? model);
    Dictionary<string, object> BuildWorkflow(ImageGenerationRequest request, string resolvedReferenceImageName);

    Dictionary<string, object> BuildWorkflow(
        ImageGenerationRequest request,
        string resolvedReferenceImageName,
        string? resolvedPreviousSceneImageName)
    {
        return BuildWorkflow(request, resolvedReferenceImageName);
    }
}
