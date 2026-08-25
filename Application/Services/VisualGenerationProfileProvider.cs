using System.Globalization;
using System.Text.Json;
using Application.Interfaces;
using Domain.Entities;
using Domain.ValueObjects;
using Microsoft.Extensions.Configuration;

namespace Application.Services;

public sealed class VisualGenerationProfileProvider : IVisualGenerationProfileProvider
{
    private const float DefaultWeight = 0.45f;
    private const float DefaultEndAt = 0.70f;
    private const int DefaultWorkflowVersion = 1;
    private const string DefaultWorkflow = "VisualIdentity";

    private readonly IConfiguration? _configuration;

    public VisualGenerationProfileProvider(IConfiguration? configuration = null)
    {
        _configuration = configuration;
    }

    /// <summary>
    /// Resolves the generation profile for a turn snapshot.
    /// Note: Character is accepted to support future per-character generation policies; current baseline resolves from configuration.
    /// </summary>
    public GenerationProfile ResolveProfile(
        Character character,
        string? workflowOverride = null,
        bool isTransition = false,
        bool isColdStart = false,
        Slot2ConditioningPolicy? slot2Policy = null)
    {
        string workflow = DefaultWorkflow;
        if (workflowOverride != null)
        {
            if (string.IsNullOrWhiteSpace(workflowOverride))
            {
                throw new InvalidOperationException("workflowOverride cannot be empty or whitespace.");
            }
            workflow = workflowOverride.Trim();
        }
        else
        {
            var configWorkflow = _configuration?["AiProviders:ImageGeneration:DefaultWorkflow"];
            if (!string.IsNullOrWhiteSpace(configWorkflow))
            {
                workflow = configWorkflow.Trim();
            }
        }

        // 1. Strict validation of WorkflowVersion (missing => default 1; present but invalid => fail-fast)
        int workflowVersion = DefaultWorkflowVersion;
        var workflowVersionStr = _configuration?["AiProviders:ImageGeneration:DefaultWorkflowVersion"];
        if (!string.IsNullOrWhiteSpace(workflowVersionStr))
        {
            if (!int.TryParse(workflowVersionStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedVer) || parsedVer <= 0)
            {
                throw new InvalidOperationException(
                    $"Invalid configuration for 'AiProviders:ImageGeneration:DefaultWorkflowVersion': '{workflowVersionStr}'. WorkflowVersion must be a positive integer greater than 0.");
            }
            workflowVersion = parsedVer;
        }

        // 2. Strict validation of IP-Adapter Weight (missing => default 0.45; present but invalid => fail-fast)
        float weight = DefaultWeight;
        var weightStr = _configuration?["AiProviders:ImageGeneration:IPAdapter:Weight"];
        if (!string.IsNullOrWhiteSpace(weightStr))
        {
            if (!float.TryParse(weightStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedWeight)
                || float.IsNaN(parsedWeight)
                || float.IsInfinity(parsedWeight)
                || parsedWeight < 0.0f
                || parsedWeight > 1.0f)
            {
                throw new InvalidOperationException(
                    $"Invalid configuration for 'AiProviders:ImageGeneration:IPAdapter:Weight': '{weightStr}'. Weight must be a valid number between 0.0 and 1.0.");
            }
            weight = parsedWeight;
        }

        // 3. Strict validation of IP-Adapter EndAt (missing => default 0.70; present but invalid => fail-fast)
        float endAt = DefaultEndAt;
        var endAtStr = _configuration?["AiProviders:ImageGeneration:IPAdapter:EndAt"];
        if (!string.IsNullOrWhiteSpace(endAtStr))
        {
            if (!float.TryParse(endAtStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedEndAt)
                || float.IsNaN(parsedEndAt)
                || float.IsInfinity(parsedEndAt)
                || parsedEndAt < 0.0f
                || parsedEndAt > 1.0f)
            {
                throw new InvalidOperationException(
                    $"Invalid configuration for 'AiProviders:ImageGeneration:IPAdapter:EndAt': '{endAtStr}'. EndAt must be a valid number between 0.0 and 1.0.");
            }
            endAt = parsedEndAt;
        }

        // 4. Resolve Context-Aware Scene Continuity Parameters from Policy & Configuration
        var effectivePolicy = slot2Policy ?? Slot2ConditioningPolicy.Default;
        var (policyWeight, policyEndAt, _) = effectivePolicy.Resolve(isColdStart, isTransition);

        float sceneWeight = (float)policyWeight;
        var sceneWeightStr = _configuration?["AiProviders:ImageGeneration:SceneContinuity:Weight"];
        if (!string.IsNullOrWhiteSpace(sceneWeightStr))
        {
            if (!float.TryParse(sceneWeightStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedSceneWeight)
                || float.IsNaN(parsedSceneWeight)
                || float.IsInfinity(parsedSceneWeight)
                || parsedSceneWeight < 0.0f
                || parsedSceneWeight > 1.0f)
            {
                throw new InvalidOperationException(
                    $"Invalid configuration for 'AiProviders:ImageGeneration:SceneContinuity:Weight': '{sceneWeightStr}'. Weight must be a valid number between 0.0 and 1.0.");
            }
            sceneWeight = parsedSceneWeight;
        }

        float sceneEndAt = (float)policyEndAt;
        var sceneEndAtStr = _configuration?["AiProviders:ImageGeneration:SceneContinuity:EndAt"];
        if (!string.IsNullOrWhiteSpace(sceneEndAtStr))
        {
            if (!float.TryParse(sceneEndAtStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedSceneEndAt)
                || float.IsNaN(parsedSceneEndAt)
                || float.IsInfinity(parsedSceneEndAt)
                || parsedSceneEndAt < 0.0f
                || parsedSceneEndAt > 1.0f)
            {
                throw new InvalidOperationException(
                    $"Invalid configuration for 'AiProviders:ImageGeneration:SceneContinuity:EndAt': '{sceneEndAtStr}'. EndAt must be a valid number between 0.0 and 1.0.");
            }
            sceneEndAt = parsedSceneEndAt;
        }

        // 5. Invariant: ParametersJson is built strictly from validated typed parameters using deterministic JSON serialization
        var parametersJson = JsonSerializer.Serialize(new
        {
            ipAdapter = new
            {
                weight,
                endAt
            },
            sceneContinuity = new
            {
                weight = sceneWeight,
                endAt = sceneEndAt
            }
        });

        return GenerationProfile.CreateDefault(
            workflow: workflow,
            workflowVersion: workflowVersion,
            parametersJson: parametersJson
        );
    }
}
