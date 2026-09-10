using Application.Exceptions;
using Domain.ValueObjects;

namespace Application.Common;

/// <summary>
/// Authoritative application-boundary validator for image generation capabilities.
/// Validates compatibility of (Model, Workflow, WorkflowVersion) tuples before provider invocation.
/// Decoupled from concrete GPU/ComfyUI provider implementations.
/// </summary>
public static class GenerationCapabilityValidator
{
    private static readonly HashSet<string> BaselineSupportedModels = new(StringComparer.OrdinalIgnoreCase)
    {
        DeterministicSeedDerivation.DefaultModel
    };

    public static bool IsSupported(ImageGenerationCapability capability, IEnumerable<string>? supportedModels = null)
    {
        if (capability == null)
            return false;

        if (string.IsNullOrWhiteSpace(capability.Model))
            return false;

        var models = supportedModels != null
            ? new HashSet<string>(supportedModels, StringComparer.OrdinalIgnoreCase)
            : BaselineSupportedModels;

        if (!models.Contains(capability.Model.Trim()))
            return false;

        var workflow = capability.Workflow?.Trim();
        if (string.IsNullOrWhiteSpace(workflow))
            return false;

        return workflow.ToLowerInvariant() switch
        {
            "visualidentity" => capability.WorkflowVersion == 1,
            "visualcontinuity" => capability.WorkflowVersion == 2,
            "texttoimage" => capability.WorkflowVersion == 1,
            _ => false
        };
    }

    public static void Validate(ImageGenerationCapability capability, IEnumerable<string>? supportedModels = null)
    {
        if (capability == null)
            throw new GpuNonTransientException("Generation capability is required.");

        if (string.IsNullOrWhiteSpace(capability.Model))
            throw new GpuNonTransientException("Model is required for image generation.");

        if (!IsSupported(capability, supportedModels))
        {
            throw new GpuNonTransientException(
                $"Generation capability '{capability}' is not supported. Workflow '{capability.Workflow}' v{capability.WorkflowVersion} is not compatible with model '{capability.Model}'.");
        }
    }
}
