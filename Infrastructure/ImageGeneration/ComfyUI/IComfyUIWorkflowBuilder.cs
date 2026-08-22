using Application.Interfaces;

namespace Infrastructure.ImageGeneration.ComfyUI;

public interface IComfyUIWorkflowBuilder
{
    string WorkflowName { get; }
    int WorkflowVersion { get; }
    Dictionary<string, object> BuildWorkflow(ImageGenerationRequest request, string resolvedReferenceImageName);
}
