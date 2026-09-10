namespace Domain.ValueObjects;

/// <summary>
/// Domain value object representing the generation capability tuple (Model, Workflow, WorkflowVersion).
/// Encapsulates model and workflow compatibility intent without infrastructure or hardware leaks.
/// </summary>
public sealed record ImageGenerationCapability
{
    public string Model { get; }
    public string Workflow { get; }
    public int WorkflowVersion { get; }

    public ImageGenerationCapability(string model, string workflow, int workflowVersion)
    {
        Model = model?.Trim() ?? string.Empty;
        Workflow = workflow?.Trim() ?? string.Empty;
        WorkflowVersion = workflowVersion;
    }

    public void Deconstruct(out string model, out string workflow, out int workflowVersion)
    {
        model = Model;
        workflow = Workflow;
        workflowVersion = WorkflowVersion;
    }

    public override string ToString() => $"Capability(Model={Model}, Workflow={Workflow}, Version={WorkflowVersion})";
}
