using System.Globalization;
using Application.Interfaces;
using Domain.Entities;
using Domain.ValueObjects;
using Microsoft.Extensions.Configuration;

namespace Application.Services;

public sealed class VisualGenerationProfileProvider : IVisualGenerationProfileProvider
{
    private readonly IConfiguration? _configuration;

    public VisualGenerationProfileProvider(IConfiguration? configuration = null)
    {
        _configuration = configuration;
    }

    /// <summary>
    /// Resolves the generation profile for a turn snapshot.
    /// Note: Character is accepted to support future per-character generation policies; current baseline resolves from configuration.
    /// </summary>
    public GenerationProfile ResolveProfile(Character character, string? workflowOverride = null)
    {
        var workflow = workflowOverride 
            ?? _configuration?["AiProviders:ImageGeneration:DefaultWorkflow"] 
            ?? "VisualIdentity";

        var workflowVersionStr = _configuration?["AiProviders:ImageGeneration:DefaultWorkflowVersion"];
        int workflowVersion = int.TryParse(workflowVersionStr, out var ver) && ver > 0 ? ver : 1;

        // Parse and validate numeric IP-Adapter parameters within valid [0.0, 1.0] bounds
        var weightStr = _configuration?["AiProviders:ImageGeneration:IPAdapter:Weight"];
        float weight = float.TryParse(weightStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedWeight)
            ? Math.Clamp(parsedWeight, 0.0f, 1.0f)
            : 0.45f;

        var endAtStr = _configuration?["AiProviders:ImageGeneration:IPAdapter:EndAt"];
        float endAt = float.TryParse(endAtStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedEndAt)
            ? Math.Clamp(parsedEndAt, 0.0f, 1.0f)
            : 0.70f;

        var parametersJson = _configuration?["AiProviders:ImageGeneration:DefaultParametersJson"]
            ?? $"{{\"ipAdapter\":{{\"weight\":{weight.ToString("0.00", CultureInfo.InvariantCulture)},\"endAt\":{endAt.ToString("0.00", CultureInfo.InvariantCulture)}}}}}";

        return GenerationProfile.CreateDefault(
            workflow: workflow,
            workflowVersion: workflowVersion,
            parametersJson: parametersJson
        );
    }
}
