namespace Domain.ValueObjects;

/// <summary>
/// Provider-agnostic generation profile containing technical parameters and execution metadata.
/// Immutable value object.
/// </summary>
public sealed record GenerationProfile(
    long Seed,
    string Model = "meinamix_meinaV11.safetensors",
    int Width = 512,
    int Height = 768,
    int Steps = 30,
    float Cfg = 7.0f,
    string Sampler = "euler_ancestral",
    string Scheduler = "karras",
    float IPAdapterWeight = 0.45f,
    float IPAdapterEndAt = 0.70f,
    string Workflow = "VisualIdentity",
    int WorkflowVersion = 1,
    string? ParametersJson = null
)
{
    public static GenerationProfile CreateDefault(
        long? seed = null,
        string? model = null,
        int? width = null,
        int? height = null,
        int? steps = null,
        float? cfg = null,
        string? sampler = null,
        string? scheduler = null,
        float? ipAdapterWeight = null,
        float? ipAdapterEndAt = null,
        string? workflow = null,
        int? workflowVersion = null,
        string? parametersJson = null)
    {
        return new GenerationProfile(
            Seed: seed ?? Random.Shared.NextInt64(1, 999999999),
            Model: model ?? "meinamix_meinaV11.safetensors",
            Width: width ?? 512,
            Height: height ?? 768,
            Steps: steps ?? 30,
            Cfg: cfg ?? 7.0f,
            Sampler: sampler ?? "euler_ancestral",
            Scheduler: scheduler ?? "karras",
            IPAdapterWeight: ipAdapterWeight ?? 0.45f,
            IPAdapterEndAt: ipAdapterEndAt ?? 0.70f,
            Workflow: workflow ?? "VisualIdentity",
            WorkflowVersion: workflowVersion ?? 1,
            ParametersJson: parametersJson
        );
    }
}
