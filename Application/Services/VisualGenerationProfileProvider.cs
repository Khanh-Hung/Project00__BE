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

    public GenerationProfile ResolveProfile(Character character, string? workflowOverride = null)
    {
        var workflow = workflowOverride 
            ?? _configuration?["AiProviders:ImageGeneration:DefaultWorkflow"] 
            ?? "VisualIdentity";

        var workflowVersionStr = _configuration?["AiProviders:ImageGeneration:DefaultWorkflowVersion"];
        int workflowVersion = int.TryParse(workflowVersionStr, out var ver) ? ver : 1;

        // Resolve IP-Adapter parameter weights from configuration (or defaults)
        var ipAdapterWeight = _configuration?["AiProviders:ImageGeneration:IPAdapter:Weight"] ?? "0.45";
        var ipAdapterEndAt = _configuration?["AiProviders:ImageGeneration:IPAdapter:EndAt"] ?? "0.70";

        var parametersJson = _configuration?["AiProviders:ImageGeneration:DefaultParametersJson"]
            ?? $"{{\"ipAdapter\":{{\"weight\":{ipAdapterWeight},\"endAt\":{ipAdapterEndAt}}}}}";

        return GenerationProfile.CreateDefault(
            workflow: workflow,
            workflowVersion: workflowVersion,
            parametersJson: parametersJson
        );
    }
}
